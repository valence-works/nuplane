#define _DARWIN_C_SOURCE

#include <fcntl.h>
#include <dirent.h>
#include <errno.h>
#include <stdint.h>
#include <string.h>
#include <sys/attr.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

/*
 * Fixed-signature bridge for openat's optional mode argument. Calling the
 * variadic libc entry point directly from managed code does not preserve the
 * Apple arm64 variadic calling convention. Keep this shim intentionally small:
 * the caller supplies the already-validated parent-relative path, flags, and
 * mode; libc receives a normal C call and handles its own variadic ABI.
 */
__attribute__((visibility("default")))
int nuplane_openat_create(int directory_fd, const char *path, int flags, unsigned int mode)
{
    return openat(directory_fd, path, flags, (mode_t)mode);
}

/*
 * Report APFS lookup semantics for an already-held directory descriptor. The
 * filesystem type and case bit come from descriptor-based native metadata;
 * this function never resolves a caller-provided path.
 */
__attribute__((visibility("default")))
int nuplane_apfs_name_profile(int directory_fd, int *case_sensitive, int *normalization_insensitive)
{
    struct statfs file_system;
    if (fstatfs(directory_fd, &file_system) != 0)
        return -1;
    if (strcmp(file_system.f_fstypename, "apfs") != 0) {
        errno = ENOTSUP;
        return -1;
    }

    struct attrlist requested;
    memset(&requested, 0, sizeof(requested));
    requested.bitmapcount = ATTR_BIT_MAP_COUNT;
    /* getattrlist requires the volume-info group whenever volume attributes are requested. */
    requested.volattr = ATTR_VOL_INFO | ATTR_VOL_CAPABILITIES;

    struct {
        uint32_t length;
        vol_capabilities_attr_t value;
    } result;
    memset(&result, 0, sizeof(result));
    if (fgetattrlist(directory_fd, &requested, &result, sizeof(result), 0) != 0)
        return -1;

    const uint32_t case_flag = VOL_CAP_FMT_CASE_SENSITIVE;
    if (result.length < sizeof(result) || (result.value.valid[VOL_CAPABILITIES_FORMAT] & case_flag) == 0) {
        errno = ENOTSUP;
        return -1;
    }

    *case_sensitive = (result.value.capabilities[VOL_CAPABILITIES_FORMAT] & case_flag) != 0;
    /* APFS lookup is normalization-insensitive while preserving stored spelling. */
    *normalization_insensitive = 1;
    return 0;
}

/*
 * Find the unique direct entry for an already-open regular file or directory.
 * Enumeration uses a new open description so it cannot consume an offset on
 * the caller's held directory handle. Names are copied exactly as returned by
 * readdir. Directory link counts are deliberately not constrained: they vary
 * with child-directory count and are not a no-hard-link check for directories.
 */
static int find_entry_name(int parent_fd, int target_fd, int target_is_directory, char *name, size_t capacity)
{
    struct stat parent_before;
    struct stat target_before;
    if (fstat(parent_fd, &parent_before) != 0 || fstat(target_fd, &target_before) != 0)
        return -1;
    if (!S_ISDIR(parent_before.st_mode) ||
        (target_is_directory ? !S_ISDIR(target_before.st_mode) : (!S_ISREG(target_before.st_mode) || target_before.st_nlink != 1)) ||
        parent_before.st_dev != target_before.st_dev) {
        errno = EINVAL;
        return -1;
    }

    int enumeration_fd = openat(parent_fd, ".", O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (enumeration_fd < 0)
        return -1;

    struct stat opened_parent;
    if (fstat(enumeration_fd, &opened_parent) != 0) {
        int saved_errno = errno;
        close(enumeration_fd);
        errno = saved_errno;
        return -1;
    }
    if (opened_parent.st_dev != parent_before.st_dev || opened_parent.st_ino != parent_before.st_ino) {
        close(enumeration_fd);
        errno = ESTALE;
        return -1;
    }

    DIR *stream = fdopendir(enumeration_fd);
    if (stream == NULL) {
        int saved_errno = errno;
        close(enumeration_fd);
        errno = saved_errno;
        return -1;
    }

    int matched = 0;
    int result = -1;
    struct dirent *entry;
    for (;;) {
        errno = 0;
        entry = readdir(stream);
        if (entry == NULL) {
            if (errno != 0)
                goto cleanup;
            break;
        }
        if (entry->d_ino != target_before.st_ino || strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0)
            continue;

        struct stat candidate;
        if (fstatat(parent_fd, entry->d_name, &candidate, AT_SYMLINK_NOFOLLOW) != 0)
            goto cleanup;
        if (candidate.st_dev != target_before.st_dev || candidate.st_ino != target_before.st_ino ||
            (target_is_directory ? !S_ISDIR(candidate.st_mode) : (!S_ISREG(candidate.st_mode) || candidate.st_nlink != 1)))
            continue;

        if (++matched != 1) {
            errno = EMLINK;
            goto cleanup;
        }

        size_t length = strlen(entry->d_name);
        if (length == 0 || length + 1 > capacity) {
            errno = ENAMETOOLONG;
            goto cleanup;
        }
        memcpy(name, entry->d_name, length + 1);
    }
    if (matched != 1) {
        errno = ESTALE;
        goto cleanup;
    }

    struct stat parent_after;
    struct stat target_after;
    struct stat named_after;
    if (fstat(parent_fd, &parent_after) != 0 || fstat(target_fd, &target_after) != 0 ||
        fstatat(parent_fd, name, &named_after, AT_SYMLINK_NOFOLLOW) != 0)
        goto cleanup;
    if (parent_after.st_dev != parent_before.st_dev || parent_after.st_ino != parent_before.st_ino ||
        target_after.st_dev != target_before.st_dev || target_after.st_ino != target_before.st_ino ||
        (target_is_directory ? !S_ISDIR(target_after.st_mode) : (!S_ISREG(target_after.st_mode) || target_after.st_nlink != 1)) ||
        named_after.st_dev != target_before.st_dev || named_after.st_ino != target_before.st_ino ||
        (target_is_directory ? !S_ISDIR(named_after.st_mode) : (!S_ISREG(named_after.st_mode) || named_after.st_nlink != 1))) {
        errno = ESTALE;
        goto cleanup;
    }
    result = 0;

cleanup:
    {
        int saved_errno = errno;
        if (closedir(stream) != 0 && result == 0)
            return -1;
        errno = saved_errno;
    }
    return result;
}

__attribute__((visibility("default")))
int nuplane_find_entry_name(int parent_fd, int file_fd, char *name, size_t capacity)
{
    return find_entry_name(parent_fd, file_fd, 0, name, capacity);
}

__attribute__((visibility("default")))
int nuplane_find_directory_entry_name(int parent_fd, int directory_fd, char *name, size_t capacity)
{
    return find_entry_name(parent_fd, directory_fd, 1, name, capacity);
}
