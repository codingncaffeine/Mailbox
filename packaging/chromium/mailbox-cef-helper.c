/* Chromium's helper processes for the reading pane: renderer, GPU, network, zygote.
 *
 * Native and single-threaded because the Linux sandbox enters its namespaces with unshare(),
 * which a multi-threaded process is refused, and every .NET process is multi-threaded. It does
 * nothing but hand control to libcef: every handler Mailbox registers lives in the browser process.
 *
 * `--mailbox-probe-sandbox` answers whether Chromium's sandbox can work here, before Chromium is
 * started, because Chromium aborts the whole application when it finds none. It walks the same
 * steps the sandbox does — new user, PID and network namespaces, the ID maps written, the empty
 * root entered — because making the namespace is not the test: Ubuntu 24.04 allows that much to
 * any program and refuses the maps that follow unless an AppArmor profile names the program.
 * Exit 0 when every step works, 1 when any is refused.
 *
 * Kept to calls every supported distribution's C library has: built on a current one, it must
 * still start on Debian 12 and Ubuntu 22.04 (glibc 2.36 and 2.35), so nothing here may pull in a
 * newer symbol than the start-up code itself does. */
#define _GNU_SOURCE
#include <fcntl.h>
#include <sched.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

typedef struct { int argc; char **argv; } cef_main_args_t;
extern const char *cef_api_hash(int version, int entry);
extern int cef_execute_process(const cef_main_args_t *args, void *application, void *windows_sandbox_info);

static int put(const char *path, const char *text)
{
    int fd = open(path, O_WRONLY);
    if (fd < 0) return -1;
    ssize_t length = (ssize_t)strlen(text);
    int ok = write(fd, text, (size_t)length) == length;
    close(fd);
    return ok ? 0 : -1;
}

static int probe_sandbox(void)
{
    uid_t uid = getuid();
    gid_t gid = getgid();

    pid_t child = fork();
    if (child < 0) return 1;
    if (child == 0)
    {
        char map[64];
        if (unshare(CLONE_NEWUSER | CLONE_NEWPID | CLONE_NEWNET) != 0) _exit(1);
        if (put("/proc/self/setgroups", "deny") != 0) _exit(1);
        snprintf(map, sizeof map, "0 %u 1", (unsigned)uid);
        if (put("/proc/self/uid_map", map) != 0) _exit(1);
        snprintf(map, sizeof map, "0 %u 1", (unsigned)gid);
        if (put("/proc/self/gid_map", map) != 0) _exit(1);
        if (chroot("/proc/self/fdinfo") != 0 || chdir("/") != 0) _exit(1);
        _exit(0);
    }

    int status = 0;
    return waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0 ? 0 : 1;
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--mailbox-probe-sandbox") == 0) return probe_sandbox();

    cef_api_hash(15400, 0); /* the API version the binding in the browser process speaks */
    cef_main_args_t args = { argc, argv };
    return cef_execute_process(&args, 0, 0);
}
