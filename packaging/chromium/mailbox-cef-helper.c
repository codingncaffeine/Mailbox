/* Chromium's helper processes for the reading pane: renderer, GPU, network, zygote.
 *
 * Native and single-threaded because the Linux sandbox enters its namespaces with unshare(),
 * which a multi-threaded process is refused, and every .NET process is multi-threaded. It does
 * nothing but hand control to libcef: every handler Mailbox registers lives in the browser process.
 *
 * `--mailbox-probe-sandbox` answers whether the sandbox can work here, before Chromium is started,
 * because Chromium aborts the whole application when it finds none: 0 when this program may make a
 * user namespace, 2 when the setuid chrome-sandbox beside it is root's, 1 for neither. */
#define _GNU_SOURCE
#include <libgen.h>
#include <limits.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

typedef struct { int argc; char **argv; } cef_main_args_t;
extern const char *cef_api_hash(int version, int entry);
extern int cef_execute_process(const cef_main_args_t *args, void *application, void *windows_sandbox_info);

static int probe_sandbox(void)
{
    pid_t child = fork();
    if (child == 0) _exit(unshare(CLONE_NEWUSER) == 0 ? 0 : 1);
    int status = 0;
    if (child > 0 && waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0)
        return 0;

    char self[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", self, sizeof self - 1);
    if (n <= 0) return 1;
    self[n] = '\0';
    char sandbox[PATH_MAX];
    snprintf(sandbox, sizeof sandbox, "%s/chrome-sandbox", dirname(self));
    struct stat st;
    if (stat(sandbox, &st) != 0 || st.st_uid != 0 || !(st.st_mode & S_ISUID)) return 1;

    /* A setuid helper is no use where privileges can never be gained (the hardened launcher's
       NoNewPrivileges): Chromium would try it and abort. */
    FILE *self_status = fopen("/proc/self/status", "r");
    char line[256];
    int no_new_privs = 0;
    while (self_status && fgets(line, sizeof line, self_status))
        if (strncmp(line, "NoNewPrivs:", 11) == 0) no_new_privs = atoi(line + 11);
    if (self_status) fclose(self_status);
    return no_new_privs ? 1 : 2;
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--mailbox-probe-sandbox") == 0) return probe_sandbox();

    cef_api_hash(15400, 0); /* the API version the binding in the browser process speaks */
    cef_main_args_t args = { argc, argv };
    return cef_execute_process(&args, 0, 0);
}
