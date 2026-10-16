#include <stddef.h>
#include <stdio.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#if defined(__x86_64__)
#define NUPLANE_ARCHITECTURE "x64"
#elif defined(__arm64__) || defined(__aarch64__)
#define NUPLANE_ARCHITECTURE "arm64"
#else
#error "The Darwin stat ABI canary supports only x64 and arm64."
#endif

static void print_metadata(const struct stat *metadata)
{
    printf("{\"Device\":%d,\"Mode\":%u,\"LinkCount\":%u,\"Inode\":%llu,\"Length\":%lld}",
        metadata->st_dev,
        (unsigned int)metadata->st_mode,
        (unsigned int)metadata->st_nlink,
        (unsigned long long)metadata->st_ino,
        (long long)metadata->st_size);
}

int main(int argc, char **argv)
{
    if (argc != 2)
        return 2;
    int directory = open(argv[1], O_RDONLY | O_DIRECTORY);
    if (directory < 0) {
        perror("open owned directory");
        return 1;
    }
    struct stat by_handle;
    struct stat by_name;
    if (fstat(directory, &by_handle) != 0 ||
        fstatat(directory, ".", &by_name, AT_SYMLINK_NOFOLLOW) != 0) {
        perror("stat owned directory");
        close(directory);
        return 1;
    }
    close(directory);
    printf(
        "{\"Architecture\":\"%s\",\"Size\":%zu,\"Device\":%zu,\"Mode\":%zu,\"LinkCount\":%zu,\"Inode\":%zu,\"Length\":%zu,\"ByHandle\":",
        NUPLANE_ARCHITECTURE,
        sizeof(struct stat),
        offsetof(struct stat, st_dev),
        offsetof(struct stat, st_mode),
        offsetof(struct stat, st_nlink),
        offsetof(struct stat, st_ino),
        offsetof(struct stat, st_size));
    print_metadata(&by_handle);
    printf(",\"ByName\":");
    print_metadata(&by_name);
    printf("}\n");
    return 0;
}
