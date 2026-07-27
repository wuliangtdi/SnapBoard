#include <CoreFoundation/CoreFoundation.h>
#include <CoreGraphics/CoreGraphics.h>
#include <inttypes.h>
#include <libproc.h>
#include <mach/mach_time.h>
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

    struct proc_taskinfo task_info = {0};
    int task_info_size = proc_pidinfo(
        pid,
        PROC_PIDTASKINFO,
        0,
        &task_info,
        sizeof(task_info));
    int file_descriptor_bytes = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, NULL, 0);
    if (task_info_size != sizeof(task_info) || file_descriptor_bytes < 0)
    {
        return 2;
    }

    printf(
        "%" PRIu64 ",%" PRIu64 ",%" PRIu64 ",%" PRIu64 ",%" PRIu64
        ",%" PRIu64 ",%" PRIu64 ",%d,%d\n",
        usage.ri_resident_size,
        usage.ri_phys_footprint,
        usage.ri_lifetime_max_phys_footprint,
        usage.ri_user_time,
        usage.ri_system_time,
        usage.ri_energy_nj,
        usage.ri_interrupt_wkups,
        task_info.pti_threadnum,
        (int)((unsigned long)file_descriptor_bytes / PROC_PIDLISTFD_SIZE));
    return 0;
}

static int print_timestamp(void)
{
    mach_timebase_info_data_t timebase = {0};
    if (mach_timebase_info(&timebase) != KERN_SUCCESS || timebase.denom == 0)
    {
        return 2;
    }

    uint64_t ticks = mach_continuous_time();
    uint64_t nanoseconds = ticks * timebase.numer / timebase.denom;
    printf("%" PRIu64 "\n", nanoseconds);
    return 0;
}

static bool dictionary_number_get(
    CFDictionaryRef dictionary,
    CFStringRef key,
    int *value)
{
    CFNumberRef number = CFDictionaryGetValue(dictionary, key);
    return number != NULL &&
        CFNumberGetValue(number, kCFNumberIntType, value);
}

static bool dictionary_number_equals(CFDictionaryRef dictionary, CFStringRef key, int expected)
{
    int value = 0;
    return dictionary_number_get(dictionary, key, &value) && value == expected;
}

static bool is_application_window(CFDictionaryRef window, pid_t pid)
{
    if (!dictionary_number_equals(window, kCGWindowOwnerPID, pid))
    {
        return false;
    }

    CFDictionaryRef bounds_dictionary = CFDictionaryGetValue(window, kCGWindowBounds);
    CGRect bounds = CGRectZero;
    return bounds_dictionary != NULL &&
        CGRectMakeWithDictionaryRepresentation(bounds_dictionary, &bounds) &&
        bounds.size.width >= 100 && bounds.size.height >= 100;
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
        if (!is_application_window(window, pid))
        {
            continue;
        }

        found = true;
        break;
    }

    CFRelease(windows);
    return found ? 0 : 1;
}

static int print_visible_window_titles(pid_t pid)
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
        if (!is_application_window(window, pid))
        {
            continue;
        }

        CFStringRef title = CFDictionaryGetValue(window, kCGWindowName);
        if (title == NULL)
        {
            continue;
        }

        char title_buffer[1024] = {0};
        if (CFStringGetCString(
                title,
                title_buffer,
                sizeof(title_buffer),
                kCFStringEncodingUTF8))
        {
            printf("%s\n", title_buffer);
            found = true;
        }
    }

    CFRelease(windows);
    return found ? 0 : 1;
}

static int print_window_info(pid_t pid)
{
    CFArrayRef windows = CGWindowListCopyWindowInfo(
        kCGWindowListOptionAll,
        kCGNullWindowID);
    if (windows == NULL)
    {
        return 2;
    }

    bool found = false;
    CFIndex count = CFArrayGetCount(windows);
    for (CFIndex index = 0; index < count; index++)
    {
        CFDictionaryRef window = CFArrayGetValueAtIndex(windows, index);
        if (!dictionary_number_equals(window, kCGWindowOwnerPID, pid))
        {
            continue;
        }

        int layer = 0;
        (void)dictionary_number_get(window, kCGWindowLayer, &layer);
        CFDictionaryRef bounds_dictionary = CFDictionaryGetValue(window, kCGWindowBounds);
        CGRect bounds = CGRectZero;
        if (bounds_dictionary == NULL ||
            !CGRectMakeWithDictionaryRepresentation(bounds_dictionary, &bounds))
        {
            continue;
        }

        char title_buffer[1024] = {0};
        CFStringRef title = CFDictionaryGetValue(window, kCGWindowName);
        if (title != NULL)
        {
            (void)CFStringGetCString(
                title,
                title_buffer,
                sizeof(title_buffer),
                kCFStringEncodingUTF8);
        }

        printf(
            "%d,%.0f,%.0f,%.0f,%.0f,%s\n",
            layer,
            bounds.origin.x,
            bounds.origin.y,
            bounds.size.width,
            bounds.size.height,
            title_buffer);
        found = true;
    }

    CFRelease(windows);
    return found ? 0 : 1;
}

int main(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(
            stderr,
            "usage: snapboard_process_probe "
            "<usage|window|window-info|window-titles|now> <pid>\n");
        return 2;
    }

    if (strcmp(argv[1], "now") == 0)
    {
        return print_timestamp();
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

    if (strcmp(argv[1], "window-titles") == 0)
    {
        return print_visible_window_titles(pid);
    }

    if (strcmp(argv[1], "window-info") == 0)
    {
        return print_window_info(pid);
    }

    return 2;
}
