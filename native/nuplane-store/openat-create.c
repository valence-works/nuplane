#define _DARWIN_C_SOURCE

#include <fcntl.h>
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
