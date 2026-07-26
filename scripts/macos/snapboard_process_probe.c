#include <CoreFoundation/CoreFoundation.h>
#include <CoreGraphics/CoreGraphics.h>
#include <inttypes.h>
#include <libproc.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>

static int print_usage(pid_t pid)
{
    struct rusage_info_v6 usage = {0};
    if (proc_pid_rusage(pid, RUSAGE_INFO_V6, (rusage_info_t *)&usage) != 0)
    {
        return 2;
    }

    printf(
        "%" PRIu64 ",%" PRIu64 ",%" PRIu64 ",%" PRIu64 ",%" PRIu64
        ",%" PRIu64 ",%" PRIu64 "\n",
        usage.ri_resident_size,
        usage.ri_phys_footprint,
        usage.ri_lifetime_max_phys_footprint,
        usage.ri_user_time,
        usage.ri_system_time,
        usage.ri_energy_nj,
        usage.ri_interrupt_wkups);
    return 0;
}

static bool dictionary_number_equals(CFDictionaryRef dictionary, CFStringRef key, int expected)
{
    CFNumberRef number = CFDictionaryGetValue(dictionary, key);
    int value = 0;
    return number != NULL &&
        CFNumberGetValue(number, kCFNumberIntType, &value) &&
        value == expected;
}

static int has_visible_window(pid_t pid)
{
    CGWindowListOption options =
        kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements;
    CFArrayRef windows = CGWindowListCopyWindowInfo(options, kCGNullWindowID);
    if (windows == NULL)
    {
        return 2;
    }

    bool found = false;
    CFIndex count = CFArrayGetCount(windows);
    for (CFIndex index = 0; index < count; index++)
    {
        CFDictionaryRef window = CFArrayGetValueAtIndex(windows, index);
        if (!dictionary_number_equals(window, kCGWindowOwnerPID, pid) ||
            !dictionary_number_equals(window, kCGWindowLayer, 0))
        {
            continue;
        }

        CFDictionaryRef boundsDictionary = CFDictionaryGetValue(window, kCGWindowBounds);
        CGRect bounds = CGRectZero;
        if (boundsDictionary != NULL &&
            CGRectMakeWithDictionaryRepresentation(boundsDictionary, &bounds) &&
            bounds.size.width >= 100 && bounds.size.height >= 100)
        {
            found = true;
            break;
        }
    }

    CFRelease(windows);
    return found ? 0 : 1;
}

int main(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: snapboard_process_probe <usage|window> <pid>\n");
        return 2;
    }

    char *end = NULL;
    long parsed_pid = strtol(argv[2], &end, 10);
    if (end == argv[2] || *end != '\0' || parsed_pid <= 0)
    {
        return 2;
    }

    pid_t pid = (pid_t)parsed_pid;
    if (strcmp(argv[1], "usage") == 0)
    {
        return print_usage(pid);
    }

    if (strcmp(argv[1], "window") == 0)
    {
        return has_visible_window(pid);
    }

    return 2;
}
