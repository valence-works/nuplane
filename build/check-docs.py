#!/usr/bin/env python3
"""Check maintained links, compile README examples, and run the sample against its walkthroughs."""

import argparse
from collections import Counter
from contextlib import contextmanager
from dataclasses import dataclass
import http.client
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import signal
import subprocess
import tempfile
import time
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parents[1]
README = Path("README.md")
GETTING_STARTED = Path("docs/wiki/Getting-Started.md")
QUICKSTART = Path("specs/014-query-package-catalog/quickstart.md")
HOST = Path("samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj")
PLUGIN = Path("samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj")
PACKAGE_ID = "Nuplane.Sample.Plugin"
PACKAGE_VERSION = "1.0.0"
PLUGIN_TYPE = f"{PACKAGE_ID}.HelloPlugin"
PACKED_PLUGIN = PLUGIN.parent / f"bin/Debug/{PACKAGE_ID}.{PACKAGE_VERSION}.nupkg"
SNIPPET_PROJECT = Path("consumer/Consumer.csproj")
PROCESS_TIMEOUT = 120
SAMPLE_TIMEOUT = 20

FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})\s*(\S*)")
HEADING = re.compile(r"^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$")
INLINE_LINK = re.compile(r"\]\(\s*(?:<([^>]+)>|((?:[^\s()]|\([^()]*\))*))")
REFERENCE_LINK = re.compile(r"^ {0,3}\[[^]]+\]:\s+(?:<([^>]+)>|(\S+))")
PROJECT_PATH = re.compile(r"`((?:src|test)/[\w./-]+)`")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def markdown(path):
    """Yield line, fence language (outside a fence: None), and text."""
    fence = None
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        match = FENCE.match(line)
        if fence is None:
            if match:
                fence = match.groups()
            else:
                yield number, None, line
        elif match and match[1][0] == fence[0][0] and len(match[1]) >= len(fence[0]) and not match[2]:
            fence = None
        else:
            yield number, fence[1], line
    require(fence is None, f"{path}: unclosed Markdown fence")


def markdown_anchors(path):
    headings = Counter()
    anchors = set()
    for _, language, line in markdown(path):
        if language is not None:
            continue
        for anchor in re.findall(r'<a\s+(?:id|name)=["\']([^"\']+)["\']', line):
            anchors.add(anchor)
        if match := HEADING.match(line):
            title = re.sub(r"\[([^]]+)\]\([^)]+\)|<[^>]+>", r"\1", match[1])
            slug = re.sub(r"[^\w\- ]", "", title.lower()).replace(" ", "-")
            duplicate = headings[slug]
            anchors.add(f"{slug}-{duplicate}" if duplicate else slug)
            headings[slug] += 1
    return anchors


def check_links():
    documents = [
        ROOT / README,
        ROOT / QUICKSTART,
        *sorted((ROOT / "docs").glob("*.md")),
        *sorted((ROOT / "docs/wiki").glob("*.md")),
    ]
    anchor_cache = {}
    checked = 0
    errors = []
    for document in documents:
        for number, language, line in markdown(document):
            if language is not None:
                continue
            links = [first or second for first, second in INLINE_LINK.findall(line)]
            if reference := REFERENCE_LINK.match(line):
                links.append(reference[1] or reference[2])
            links.extend("/" + path for path in PROJECT_PATH.findall(line))
            for link in links:
                parts = urlsplit(link)
                if parts.scheme or parts.netloc:
                    continue
                path, fragment = unquote(parts.path), unquote(parts.fragment)
                target = document.parent / path if path else document
                if path.startswith("/"):
                    target = ROOT / path.lstrip("/")
                target = target.resolve()
                checked += 1
                reason = None
                if not target.is_relative_to(ROOT) or not target.exists():
                    reason = "target does not exist in the repository"
                elif fragment and target.suffix.lower() == ".md" and not re.fullmatch(r"L\d+(?:-L\d+)?", fragment):
                    if target not in anchor_cache:
                        anchor_cache[target] = markdown_anchors(target)
                    if fragment not in anchor_cache[target]:
                        reason = f"heading #{fragment} does not exist"
                if reason:
                    errors.append(f"{document.relative_to(ROOT)}:{number}: {link}: {reason}")
    require(not errors, "\n".join(errors))
    print(f"Checked {checked} repository paths and links in {len(documents)} maintained documents.")


# Only surrounding host setup is added. The displayed code itself is compiled unchanged.
HOST_BUILDER = "var builder = WebApplication.CreateBuilder(args);"
SNIPPET_HOSTS = {
    "Desired state": (HOST_BUILDER + "\nvar services = builder.Services;\nvar configuration = builder.Configuration;", ""),
    "📦 Quick Start": ("", ""),
    "Fluent builder still works": (HOST_BUILDER, ""),
    "Reading package content": (HOST_BUILDER, 'var app = builder.Build();\napp.MapGet("/module", ReadModuleAsync);'),
    "Directory-backed local feed": (HOST_BUILDER, ""),
}


def read_snippets():
    sections = {}
    heading = None
    previous_line = None
    for number, language, line in markdown(ROOT / README):
        if language is None and (match := HEADING.match(line)):
            heading = match[1]
        if language in {"csharp", "cs", "c#"}:
            if previous_line != number - 1:
                require(heading not in sections, f"README.md:{number}: use a separate heading for each C# example")
                sections[heading] = (number, [])
            sections[heading][1].append(line)
            previous_line = number
    require(set(sections) == set(SNIPPET_HOSTS),
            f"README C# examples changed. Update the example scaffolds. "
            f"Unlisted: {set(sections) - set(SNIPPET_HOSTS)}. Missing: {set(SNIPPET_HOSTS) - set(sections)}.")
    return sections


def dotnet(fixture, *args):
    subprocess.run(
        ["dotnet", *args, "--disable-build-servers"],
        cwd=fixture,
        check=True,
        timeout=PROCESS_TIMEOUT,
        env=dotnet_environment(fixture),
    )


def dotnet_environment(fixture):
    return {
        **os.environ,
        "DOTNET_CLI_HOME": str(fixture / ".dotnet-home"),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "1",
        "DOTNET_NOLOGO": "1",
        "NUGET_PACKAGES": str(fixture / ".nuget-packages"),
    }


def restore(fixture):
    require(not fixture.exists(), f"Use a new fixture path: {fixture}")
    fixture.mkdir(parents=True)
    for name in ("src", "samples", "native"):
        shutil.copytree(ROOT / name, fixture / name, ignore=shutil.ignore_patterns("bin", "obj", "packages", ".nuplane"))
    for name in (
        "Directory.Build.props",
        "Directory.Packages.props",
        "NuGet.config",
        "global.json",
        "README.md",
        "LICENSE",
        "logo.png",
    ):
        shutil.copy2(ROOT / name, fixture / name)
    consumer = fixture / SNIPPET_PROJECT
    consumer.parent.mkdir()
    consumer.write_text("""<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../src/Nuplane/Nuplane.csproj" />
    <ProjectReference Include="../src/Nuplane.Loading/Nuplane.Loading.csproj" />
  </ItemGroup>
</Project>
""", encoding="utf-8")
    for project in (HOST, PLUGIN, SNIPPET_PROJECT):
        dotnet(fixture, "restore", str(project))


def check_snippets(fixture):
    source_path = json.dumps(str(ROOT / README), ensure_ascii=False)
    for title, (number, lines) in read_snippets().items():
        before, after = SNIPPET_HOSTS[title]
        imports = 0
        while imports < len(lines) and lines[imports].startswith("using "):
            imports += 1
        program = [
            f"#line {number} {source_path}",
            *lines[:imports],
            "#line default",
            before,
            f"#line {number + imports} {source_path}",
            *lines[imports:],
            "#line default",
            after,
        ]
        (fixture / SNIPPET_PROJECT.parent / "Program.cs").write_text("\n".join(program) + "\n", encoding="utf-8")
        print(f"Compiling README.md:{number} ({title}).", flush=True)
        dotnet(fixture, "build", str(SNIPPET_PROJECT), "--no-restore")


@dataclass(frozen=True)
class Request:
    source: str
    method: str
    path: str
    status: int


@dataclass(frozen=True)
class Walkthrough:
    startup: list[str]
    pack: list[str]
    drop: Path
    requests: list[Request]
    required_paths: set[str]


def read_walkthrough(document):
    commands = [(number, shlex.split(line)) for number, language, line in markdown(ROOT / document)
                if language == "bash" and line.strip()]
    run = [args for _, args in commands if args[:2] == ["dotnet", "run"]]
    pack = [args for _, args in commands if args[:2] == ["dotnet", "pack"]]
    mkdir = [args for _, args in commands if args[0] == "mkdir"]
    copy = [args for _, args in commands if args[0] == "cp"]
    require(len(run) == 1 and run[0][:-1] == ["dotnet", "run", "--project", str(HOST),
                                              "--no-launch-profile", "--", "--urls"],
            f"{document}: expected one repository-root sample run command")
    require(pack == [["dotnet", "pack", str(PLUGIN), "-c", "Debug"]],
            f"{document}: expected one Debug pack command for {PLUGIN}")
    require(len(mkdir) == len(copy) == 1 and mkdir[0][:2] == ["mkdir", "-p"] and len(mkdir[0]) == 3
            and len(copy[0]) == 3 and copy[0][:2] == ["cp", str(PACKED_PLUGIN)]
            and Path(mkdir[0][2]) == Path(copy[0][2]),
            f"{document}: expected a mkdir and package copy to the same feed directory")
    drop = Path(copy[0][2])
    require(not drop.is_absolute() and ".." not in drop.parts, f"{document}: package drop must stay in the fixture")

    origin = urlsplit(run[0][-1])
    require(origin.scheme == "http" and origin.hostname == "127.0.0.1" and origin.port and not origin.path,
            f"{document}: sample must use an HTTP loopback address")
    requests = []
    for number, args in commands:
        if args[0] != "curl":
            continue
        target = urlsplit(args[-1])
        source = f"{document}:{number}"
        require((target.scheme, target.netloc) == (origin.scheme, origin.netloc) and target.path.startswith("/")
                and not target.query and not target.fragment, f"{source}: expected a request to the sample")
        options = args[1:-1]
        if options == ["-fsS"]:
            method, status = "GET", 200
        elif options == ["-fsS", "-X", "POST"]:
            method, status = "POST", 200
        else:
            require(options == ["-sS", "-o", "/dev/null", "-w", r"%{http_code}\n"],
                    f"{source}: unsupported sample request options")
            method, status = "GET", 404
        requests.append(Request(source, method, target.path, status))
    required = {"/catalog/packages", "/catalog/plugins"}
    if document == QUICKSTART:
        required |= {"/catalog/load-state", "/catalog/assemblies", f"/catalog/assemblies/{PACKAGE_ID}",
                     "/catalog/assemblies/Missing.Plugin", "/nuplane/admin/packages", "/nuplane/admin/state",
                     "/nuplane/admin/load-state", "/nuplane/admin/reconcile"}
    return Walkthrough(run[0], pack[0], drop, requests, required)


def http_request(port, path, method="GET"):
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=2)
    try:
        connection.request(method, path, headers={"Accept": "application/json"})
        response = connection.getresponse()
        return response.status, response.read()
    finally:
        connection.close()


def wait_for(process, description, probe):
    deadline = time.monotonic() + SAMPLE_TIMEOUT
    while process.poll() is None and time.monotonic() < deadline:
        try:
            if result := probe():
                return result
        except OSError:
            pass
        time.sleep(0.15)
    raise ValueError(f"{description} did not become ready in {SAMPLE_TIMEOUT}s (sample exit: {process.poll()})")


def ready_port(log):
    text = log.read_text(encoding="utf-8", errors="replace")
    match = re.search(r"Now listening on: http://127\.0\.0\.1:(\d+)", text)
    if match and "Directory watcher enabled" in text:
        port = int(match[1])
        status, body = http_request(port, "/catalog/packages")
        if status == 200 and json.loads(body)["packages"] == []:
            return port
    return None


@contextmanager
def sample_host(fixture, workflow, state):
    log = state / "host.log"
    environment = {
        **dotnet_environment(fixture),
        "DOTNET_ENVIRONMENT": "Production",
        "ASPNETCORE_ENVIRONMENT": "Production",
        "Logging__LogLevel__Default": "Information",
        "Logging__LogLevel__Microsoft.AspNetCore": "Warning",
        "Nuplane__StoreRegistry__StateFilePath": str(state / "store-state.json"),
        "Nuplane__FeedResolution__PackageInstallRoot": str(state / "store"),
        "Nuplane__LockFile__Path": str(state / "nuplane.lock.json"),
        # An accepted package must come from the watcher. The poll cannot win this smoke test.
        "Nuplane__Reconciliation__PollInterval": "01:00:00",
    }
    command = [*workflow.startup[:5], "--no-build", "--no-restore", "--disable-build-servers",
               *workflow.startup[5:-1], "http://127.0.0.1:0"]
    with log.open("wb") as output:
        process = subprocess.Popen(command, cwd=fixture, env=environment, start_new_session=True,
                                   stdout=output, stderr=subprocess.STDOUT)
        try:
            yield process, wait_for(process, "the empty sample and its directory watcher", lambda: ready_port(log))
        except Exception:
            print(log.read_text(encoding="utf-8", errors="replace")[-12000:], flush=True)
            raise
        finally:
            try:
                os.killpg(process.pid, signal.SIGTERM)
                process.wait(timeout=5)
            except ProcessLookupError:
                pass
            except subprocess.TimeoutExpired:
                pass
            finally:
                # dotnet run can exit before its child. End any remaining process in the owned group.
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait(timeout=5)


def package_record(record):
    return record["packageId"] == PACKAGE_ID and record["version"] == PACKAGE_VERSION


def plugin_visible(port):
    status, body = http_request(port, "/catalog/plugins")
    return status == 200 and any(package_record(record) and record["pluginType"] == PLUGIN_TYPE
                                 for record in json.loads(body))


def check_response(request, body):
    path = request.path
    if path.endswith("/Missing.Plugin"):
        return body["packageId"] == "Missing.Plugin" and body["reason"] == "package-not-active-or-not-loaded"
    if path.endswith("/packages"):
        return any(package_record(record) and record["feedName"] and record["installPath"] for record in body["packages"])
    if path.endswith("/load-state"):
        return any(package_record(record) and record["loadedAtUtc"] for record in body["packages"])
    if path.endswith("/state"):
        return bool(body["health"] and body["correlationId"]) and "lastReconcile" in body
    if path.endswith("/reconcile"):
        return body["outcomeCode"] == "Completed"
    if path.endswith("/plugins"):
        return any(package_record(record) and record["pluginType"] == PLUGIN_TYPE for record in body)
    records = body if isinstance(body, list) else [body]
    return any(package_record(record) and record["loadedAssemblies"] and record["selectedAssemblyReferences"]
               for record in records)


def check_sample(fixture):
    dotnet(fixture, "build", str(HOST), "--no-restore")
    for session, document in enumerate((README, GETTING_STARTED, QUICKSTART)):
        workflow = read_walkthrough(document)
        if session == 0:
            dotnet(fixture, *workflow.pack[1:], "--no-restore")
        destination = (fixture / workflow.drop / PACKED_PLUGIN.name).resolve()
        require(destination.is_relative_to(fixture) and not destination.exists(),
                f"{document}: expected an unused package path in the fixture: {destination}")
        with tempfile.TemporaryDirectory(prefix="sample-", dir=fixture) as state, \
                sample_host(fixture, workflow, Path(state)) as (process, port):
            try:
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(fixture / PACKED_PLUGIN, destination)
                wait_for(process, f"{document}: the dropped package at {workflow.drop} and its plugin type",
                         lambda: plugin_visible(port))
                for request in workflow.requests:
                    status, body = http_request(port, request.path, request.method)
                    require(status == request.status,
                            f"{request.source}: {request.method} {request.path} returned {status}, expected {request.status}")
                    require(check_response(request, json.loads(body)),
                            f"{request.source}: unexpected response from {request.path}: {body!r}")
                    print(f"{request.source}: {request.method} {request.path} => {status}", flush=True)
                missing = workflow.required_paths - {request.path for request in workflow.requests}
                require(not missing, f"{document}: missing sample requests for {sorted(missing)}")
            finally:
                destination.unlink(missing_ok=True)


def verify(fixture):
    check_links()
    check_snippets(fixture)
    check_sample(fixture)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("check", choices=("links", "restore", "snippets", "sample", "verify"))
    parser.add_argument("fixture", type=Path, nargs="?", help="disposable folder outside the repository")
    args = parser.parse_args()
    try:
        if args.check == "links":
            check_links()
        else:
            require(args.fixture is not None, "A fixture path is required")
            fixture = args.fixture.resolve()
            require(not fixture.is_relative_to(ROOT), "The fixture must be outside the repository")
            actions = {"restore": restore, "snippets": check_snippets, "sample": check_sample, "verify": verify}
            actions[args.check](fixture)
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
