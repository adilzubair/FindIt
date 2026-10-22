#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincrypt.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <strsafe.h>
#include <wchar.h>
#include <commctrl.h>
#include "resource.h"

#define CAP 32768

enum { STARTING, COUNTING, COPYING, VERIFYING, WAITING_CACHE, OPENING };
static HWND window, heading, description, progress, cancelButton;
static HANDLE child;
static DWORD childPid;
static volatile LONG cancelled, stage, percentage = -1;
static BOOL animate = TRUE;
static wchar_t problem[512];
static ULONGLONG totalBytes, copiedBytes;
static DWORD manifestFiles;

static void report(LONG nextStage, LONG percent) {
    InterlockedExchange(&percentage, percent);
    InterlockedExchange(&stage, nextStage);
}

static int join(wchar_t *out, const wchar_t *a, const wchar_t *b) {
    size_t length = wcslen(a);
    const wchar_t *separator = length && (a[length - 1] == L'\\' || a[length - 1] == L'/') ? L"" : L"\\";
    return SUCCEEDED(StringCchPrintfW(out, CAP, L"%s%s%s", a, separator, b));
}

static void failure(const wchar_t *message) {
    StringCchCopyW(problem, ARRAYSIZE(problem), message);
}

static int directories(const wchar_t *path) {
    wchar_t copy[CAP];
    if (FAILED(StringCchCopyW(copy, CAP, path))) return 0;
    for (wchar_t *p = copy + 3; *p; ++p) {
        if (*p == L'\\') { *p = 0; CreateDirectoryW(copy, NULL); *p = L'\\'; }
    }
    return CreateDirectoryW(copy, NULL) || GetLastError() == ERROR_ALREADY_EXISTS;
}

static int digest(const wchar_t *path, wchar_t *hex) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (file == INVALID_HANDLE_VALUE) return 0;
    HCRYPTPROV provider = 0; HCRYPTHASH hash = 0;
    int ok = CryptAcquireContextW(&provider, NULL, NULL, PROV_RSA_AES, CRYPT_VERIFYCONTEXT) && CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash);
    BYTE *buffer = HeapAlloc(GetProcessHeap(), 0, 65536); DWORD read = 0;
    if (!buffer) ok = 0;
    while (ok) {
        if (InterlockedCompareExchange(&cancelled, 0, 0)) { ok = 0; break; }
        if (!ReadFile(file, buffer, 65536, &read, NULL)) { ok = 0; break; }
        if (!read) break;
        if (!CryptHashData(hash, buffer, read, 0)) { ok = 0; break; }
    }
    BYTE bytes[32]; DWORD size = sizeof bytes;
    if (ok) ok = CryptGetHashParam(hash, HP_HASHVAL, bytes, &size, 0);
    if (ok) { for (DWORD i = 0; i < 32; ++i) StringCchPrintfW(hex + i * 2, 3, L"%02x", bytes[i]); hex[64] = 0; }
    if (buffer) HeapFree(GetProcessHeap(), 0, buffer);
    if (hash) CryptDestroyHash(hash);
    if (provider) CryptReleaseContext(provider, 0);
    CloseHandle(file); return ok;
}

static int count_tree(const wchar_t *source) {
    wchar_t pattern[CAP];
    if (!join(pattern, source, L"*")) return 0;
    WIN32_FIND_DATAW entry; HANDLE enumeration = FindFirstFileW(pattern, &entry);
    if (enumeration == INVALID_HANDLE_VALUE) return 0;
    int ok = 1; DWORD error;
    do {
        if (cancelled) { ok = 0; break; }
        if (!wcscmp(entry.cFileName, L".") || !wcscmp(entry.cFileName, L"..")) continue;
        if (!_wcsicmp(entry.cFileName, L".complete")) continue;
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) { ok = 0; break; }
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            wchar_t path[CAP];
            if (!join(path, source, entry.cFileName) || !count_tree(path)) { ok = 0; break; }
        } else totalBytes += ((ULONGLONG)entry.nFileSizeHigh << 32) | entry.nFileSizeLow;
    } while (FindNextFileW(enumeration, &entry));
    error = GetLastError(); FindClose(enumeration);
    return ok && error == ERROR_NO_MORE_FILES;
}

static DWORD CALLBACK copy_progress(LARGE_INTEGER size, LARGE_INTEGER transferred, LARGE_INTEGER streamSize,
    LARGE_INTEGER streamTransferred, DWORD stream, DWORD reason, HANDLE source, HANDLE target, LPVOID data) {
    (void)size; (void)streamSize; (void)streamTransferred; (void)stream; (void)reason; (void)source; (void)target; (void)data;
    if (cancelled) return PROGRESS_CANCEL;
    if (totalBytes) InterlockedExchange(&percentage, (LONG)((copiedBytes + transferred.QuadPart) * 1000 / totalBytes));
    return PROGRESS_CONTINUE;
}

static int copy_tree(const wchar_t *source, const wchar_t *target) {
    wchar_t pattern[CAP];
    if (!directories(target) || !join(pattern, source, L"*")) return 0;
    WIN32_FIND_DATAW entry; HANDLE enumeration = FindFirstFileW(pattern, &entry);
    if (enumeration == INVALID_HANDLE_VALUE) return 0;
    int ok = 1; DWORD error;
    do {
        if (cancelled) { ok = 0; break; }
        if (!wcscmp(entry.cFileName, L".") || !wcscmp(entry.cFileName, L"..")) continue;
        if (!_wcsicmp(entry.cFileName, L".complete")) continue;
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) { ok = 0; break; }
        wchar_t from[CAP], to[CAP];
        if (!join(from, source, entry.cFileName) || !join(to, target, entry.cFileName)) { ok = 0; break; }
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ok = copy_tree(from, to);
        else {
            ok = CopyFileExW(from, to, copy_progress, NULL, NULL, 0);
            if (ok) copiedBytes += ((ULONGLONG)entry.nFileSizeHigh << 32) | entry.nFileSizeLow;
        }
        if (!ok) break;
    } while (FindNextFileW(enumeration, &entry));
    error = GetLastError(); FindClose(enumeration);
    return ok && error == ERROR_NO_MORE_FILES;
}

static int verify_manifest(const wchar_t *folder) {
    wchar_t manifest[CAP]; if (!join(manifest, folder, L"package.sha256")) return 0;
    HANDLE file = CreateFileW(manifest, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER size;
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 4 * 1024 * 1024) { CloseHandle(file); return 0; }
    char *text = HeapAlloc(GetProcessHeap(), 0, (SIZE_T)size.QuadPart + 1); DWORD read = 0;
    int ok = text && ReadFile(file, text, (DWORD)size.QuadPart, &read, NULL) && read == size.QuadPart;
    CloseHandle(file);
    if (!ok) { if (text) HeapFree(GetProcessHeap(), 0, text); return 0; }
    text[read] = 0;
    DWORD count = 0;
    for (char *p = text; *p; ++p) if (*p == '\n') ++count;
    if (read && text[read - 1] != '\n') ++count;
    manifestFiles = count;
    DWORD checked = 0;
    for (char *line = text; ok && *line;) {
        if (cancelled) { ok = 0; break; }
        char *end = strchr(line, '\n'); if (end) *end = 0;
        size_t length = strlen(line); if (length && line[length - 1] == '\r') line[--length] = 0;
        if (length) {
            if (length < 67 || line[64] != ' ' || line[65] != ' ') { ok = 0; break; }
            wchar_t relative[CAP], expected[65], actual[65], path[CAP];
            if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, line + 66, -1, relative, CAP)) { ok = 0; break; }
            // Only bundle-relative paths; neither a manifest nor a junction may redirect runtime caching.
            if (relative[0] == L'\\' || relative[0] == L'/' || wcschr(relative, L':') || wcsstr(relative, L"..") || !_wcsicmp(relative, L".complete")) { ok = 0; break; }
            for (int i = 0; i < 64; ++i) expected[i] = (wchar_t)line[i]; expected[64] = 0;
            if (!join(path, folder, relative) || !digest(path, actual) || _wcsicmp(expected, actual)) { ok = 0; break; }
            if (manifestFiles) InterlockedExchange(&percentage, (LONG)(++checked * 1000 / manifestFiles));
        }
        if (!end) break; line = end + 1;
    }
    HeapFree(GetProcessHeap(), 0, text); return ok;
}

static int quote(wchar_t *out, size_t *position, const wchar_t *argument) {
    if (*position + 2 >= CAP) return 0; out[(*position)++] = L'"';
    unsigned slashes = 0;
    for (const wchar_t *p = argument; ; ++p) {
        if (*p == L'\\') { ++slashes; continue; }
        unsigned count = (*p == L'"' || !*p) ? slashes * 2 : slashes;
        while (count--) { if (*position + 2 >= CAP) return 0; out[(*position)++] = L'\\'; }
        slashes = 0;
        if (!*p) break;
        if (*p == L'"') { if (*position + 2 >= CAP) return 0; out[(*position)++] = L'\\'; }
        if (*position + 2 >= CAP) return 0; out[(*position)++] = *p;
    }
    out[(*position)++] = L'"'; out[*position] = 0; return 1;
}

typedef struct { DWORD magic, pid; FILETIME created; DWORD volume, fileHigh, fileLow; } InstanceRecord;
static wchar_t root[CAP], local[CAP], instancePath[CAP];

static int file_identity(const wchar_t *path, BY_HANDLE_FILE_INFORMATION *identity) {
    HANDLE file = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 0;
    int ok = GetFileInformationByHandle(file, identity); CloseHandle(file); return ok;
}

typedef struct { DWORD pid; HWND found; } WindowSearch;
static BOOL CALLBACK find_window(HWND candidate, LPARAM parameter) {
    WindowSearch *search = (WindowSearch *)parameter;
    DWORD pid = 0; GetWindowThreadProcessId(candidate, &pid);
    if (pid == search->pid && IsWindowVisible(candidate) && !GetWindow(candidate, GW_OWNER)) {
        search->found = candidate; return FALSE;
    }
    return TRUE;
}

static HWND app_window(DWORD pid) {
    WindowSearch search = {pid, NULL}; EnumWindows(find_window, (LPARAM)&search); return search.found;
}

static void activate(HWND target) {
    if (!target) return;
    if (IsIconic(target)) ShowWindowAsync(target, SW_RESTORE);
    SetForegroundWindow(target);
    FLASHWINFO flash = {sizeof flash, target, FLASHW_TRAY, 2, 0}; FlashWindowEx(&flash);
}

// A disposable process/file-identity record keeps subsequent clicks attached to the
// same application after this USB-hosted launcher has exited. No library content
// or filenames are stored on the host, and PID reuse cannot target another app.
static int existing_app(void) {
    HANDLE file = CreateFileW(instancePath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 0;
    InstanceRecord record; DWORD read = 0;
    int ok = ReadFile(file, &record, sizeof record, &read, NULL) && read == sizeof record && record.magic == 0x46494e32;
    CloseHandle(file); if (!ok) return 0;
    HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, record.pid);
    if (!process) return 0;
    FILETIME created, exited, kernel, user;
    wchar_t path[CAP]; DWORD length = CAP;
    ok = WaitForSingleObject(process, 0) == WAIT_TIMEOUT &&
        GetProcessTimes(process, &created, &exited, &kernel, &user) &&
        !CompareFileTime(&created, &record.created) &&
        QueryFullProcessImageNameW(process, 0, path, &length);
    if (ok) {
        const wchar_t *name = wcsrchr(path, L'\\');
        BY_HANDLE_FILE_INFORMATION identity;
        // Compare actual files, not path spelling: packaged Windows hosts can
        // redirect LOCALAPPDATA while reporting a different physical image path.
        ok = name && !_wcsicmp(name + 1, L"FindIt.App.exe") && file_identity(path, &identity) &&
            record.volume == identity.dwVolumeSerialNumber && record.fileHigh == identity.nFileIndexHigh && record.fileLow == identity.nFileIndexLow;
    }
    CloseHandle(process);
    if (ok) activate(app_window(record.pid));
    return ok;
}

static int remember_app(HANDLE process, DWORD pid, const wchar_t *app) {
    FILETIME exited, kernel, user;
    InstanceRecord record = {0x46494e32, pid, {0, 0}, 0, 0, 0};
    BY_HANDLE_FILE_INFORMATION identity;
    if (!GetProcessTimes(process, &record.created, &exited, &kernel, &user) || !file_identity(app, &identity)) return 0;
    record.volume = identity.dwVolumeSerialNumber; record.fileHigh = identity.nFileIndexHigh; record.fileLow = identity.nFileIndexLow;
    HANDLE file = CreateFileW(instancePath, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 0;
    DWORD written = 0;
    int ok = WriteFile(file, &record, sizeof record, &written, NULL) && written == sizeof record && FlushFileBuffers(file);
    CloseHandle(file); return ok;
}

static DWORD WINAPI launch(LPVOID unused) {
    (void)unused;
    wchar_t versionPath[CAP], version[65], source[CAP], cache[CAP], manifest[CAP], hash[65], app[CAP], complete[CAP];
    // Check the running instance before reading anything from a slow USB drive.
    if (existing_app()) return 0;
    if (!join(versionPath, root, L"FindIt\\app\\current.txt")) return 1;
    HANDLE versionFile = CreateFileW(versionPath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (versionFile == INVALID_HANDLE_VALUE) { failure(L"Keep FindIt.exe beside its FindIt folder. Restore the complete portable package and try again."); return 1; }
    char bytes[65] = {0}; DWORD read = 0; ReadFile(versionFile, bytes, 64, &read, NULL); CloseHandle(versionFile);
    size_t length = 0;
    while (length < read && ((bytes[length] >= '0' && bytes[length] <= '9') || (bytes[length] >= 'a' && bytes[length] <= 'z') || (bytes[length] >= 'A' && bytes[length] <= 'Z') || bytes[length] == '.' || bytes[length] == '-' || bytes[length] == '_')) { version[length] = bytes[length]; ++length; }
    version[length] = 0;
    if (!length || wcsstr(version, L"..")) { failure(L"The application version file is damaged. Restore the portable package."); return 1; }
    wchar_t relative[128]; StringCchPrintfW(relative, 128, L"FindIt\\app\\%s", version);
    if (!join(source, root, relative) || !join(manifest, source, L"package.sha256") || !digest(manifest, hash)) { failure(L"The portable application manifest is missing or unreadable."); return 1; }
    StringCchPrintfW(relative, 128, L"FindIt\\runtime\\%s-%.16s", version, hash);
    if (!join(cache, local, relative) || !join(complete, cache, L".complete")) return 1;
    // Separate libraries may share one immutable runtime cache. Serialize its
    // population as well as serializing launches for each individual library.
    wchar_t cacheName[160]; StringCchPrintfW(cacheName, ARRAYSIZE(cacheName), L"Local\\FindIt.Cache.%s.%.16s", version, hash);
    HANDLE cacheLock = CreateMutexW(NULL, FALSE, cacheName);
    if (!cacheLock) { failure(L"Windows could not prepare FindIt. Close FindIt and try again."); return 1; }
    DWORD wait;
    while ((wait = WaitForSingleObject(cacheLock, 100)) == WAIT_TIMEOUT) {
        report(WAITING_CACHE, -1);
        if (cancelled) { CloseHandle(cacheLock); return 1; }
    }
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) { CloseHandle(cacheLock); return 1; }
    int verified = 1;
    if (GetFileAttributesW(complete) == INVALID_FILE_ATTRIBUTES) {
        report(COUNTING, -1);
        verified = count_tree(source);
        if (verified) { report(COPYING, 0); verified = copy_tree(source, cache); }
        if (verified) { report(VERIFYING, 0); verified = verify_manifest(cache); }
        if (verified && !cancelled) {
            HANDLE marker = CreateFileW(complete, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
            verified = marker != INVALID_HANDLE_VALUE;
            if (verified) CloseHandle(marker);
        } else verified = 0;
    }
    ReleaseMutex(cacheLock); CloseHandle(cacheLock);
    if (!verified) { failure(L"FindIt could not finish setting up. Keep the drive connected and make sure this PC has free space, then try again."); return 1; }
    if (cancelled) return 1;
    report(OPENING, -1);
    if (!join(app, cache, L"FindIt.App.exe")) return 1;
    wchar_t arguments[CAP] = {0}; size_t position = 0;
    if (!quote(arguments, &position, app)) return 1;
    StringCchCatW(arguments, CAP, L" --root "); position = wcslen(arguments);
    if (!quote(arguments, &position, root)) return 1;
    // Pass through optional diagnostic/display flags using proper Windows argument quoting.
    int argc = 0; LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv) {
        for (int i = 1; i < argc; ++i) { if (position + 2 >= CAP) break; arguments[position++] = L' '; if (!quote(arguments, &position, argv[i])) { LocalFree(argv); return 1; } }
        LocalFree(argv);
    }
    STARTUPINFOW startup = {0}; startup.cb = sizeof startup;
    PROCESS_INFORMATION process = {0};
    if (cancelled) return 1;
    if (!CreateProcessW(app, arguments, NULL, NULL, FALSE, 0, NULL, cache, &startup, &process)) { failure(L"FindIt could not start. Restore the complete portable application package."); return 1; }
    CloseHandle(process.hThread);
    childPid = process.dwProcessId; child = process.hProcess;
    // Keep the startup guard until the first real window appears. If saving the
    // record fails, close only our newly created child to avoid untracked copies.
    if (!remember_app(child, childPid, app)) {
        TerminateProcess(child, 1); WaitForSingleObject(child, INFINITE);
        failure(L"FindIt could not save its startup state on this PC. Check that your Windows profile has free space and try again.");
        return 1;
    }
    return 0;
}

static void update_status(void) {
    const wchar_t *title = L"Starting FindIt...", *detail = L"Keep your drive connected.";
    switch (stage) {
        case COUNTING: title = L"Setting up FindIt on this PC"; detail = L"Getting ready for the first launch. Keep your drive connected."; break;
        case COPYING: title = L"Setting up FindIt on this PC"; detail = L"Copying application files. This is only needed once per update."; break;
        case VERIFYING: title = L"Checking application files"; detail = L"Making sure FindIt is ready. Your library stays on the drive."; break;
        case WAITING_CACHE: title = L"FindIt is already getting ready"; detail = L"Waiting for application setup to finish on this PC."; break;
        case OPENING: title = L"Opening FindIt..."; detail = L"Your library window will appear shortly."; break;
    }
    if (cancelled) { title = L"Stopping setup..."; detail = L"You can open FindIt again when you are ready."; }
    wchar_t label[160]; LONG value = percentage;
    if (value >= 0 && !cancelled) StringCchPrintfW(label, ARRAYSIZE(label), L"%s (%ld%%)", title, value / 10);
    else StringCchCopyW(label, ARRAYSIZE(label), title);
    SetWindowTextW(heading, label); SetWindowTextW(description, detail);
    // Use a real byte/file percentage; an unknown-length phase remains an
    // accessible indeterminate bar, without invented elapsed-time estimates.
    LONG_PTR style = GetWindowLongPtrW(progress, GWL_STYLE);
    int marquee = value < 0 && !cancelled && animate;
    if (!!(style & PBS_MARQUEE) != marquee) {
        SetWindowLongPtrW(progress, GWL_STYLE, marquee ? style | PBS_MARQUEE : style & ~PBS_MARQUEE);
        SendMessageW(progress, PBM_SETMARQUEE, marquee, 40);
    }
    if (value >= 0) SendMessageW(progress, PBM_SETPOS, value, 0);
    EnableWindow(cancelButton, !cancelled && stage != OPENING);
}

static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
        case WM_COMMAND:
            if (LOWORD(wParam) != IDCANCEL) break;
            // Fall through: Cancel and the title-bar close button share one path.
        case WM_CLOSE:
            if (stage != OPENING) { InterlockedExchange(&cancelled, 1); update_status(); }
            else ShowWindow(hwnd, SW_MINIMIZE);
            return 0;
        case WM_QUERYENDSESSION: InterlockedExchange(&cancelled, 1); return TRUE;
        case WM_CTLCOLORSTATIC:
            SetTextColor((HDC)wParam, GetSysColor(COLOR_WINDOWTEXT));
            SetBkColor((HDC)wParam, GetSysColor(COLOR_WINDOW));
            return (LRESULT)GetSysColorBrush(COLOR_WINDOW);
        case WM_TIMER: update_status(); return 0;
        case WM_SETTINGCHANGE:
            SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animate, 0);
            update_status(); return 0;
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show) {
    (void)previous; (void)commandLine; (void)show;
    SetCurrentProcessExplicitAppUserModelID(L"FindIt.Desktop");
    SetProcessDPIAware();
    SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animate, 0);
    DWORD moduleLength = GetModuleFileNameW(NULL, root, CAP);
    if (!moduleLength || moduleLength >= CAP) return 1;
    wchar_t *slash = wcsrchr(root, L'\\'); if (!slash) return 1;
    // D: is relative to a per-drive current directory; D:\ always means root.
    if (slash > root && slash[-1] == L':') slash[1] = 0; else *slash = 0;

    // Stable per-library identity before USB I/O. Fold ASCII casing and ignore
    // a trailing separator so repeat invocations share the same startup guard.
    ULONGLONG identity = 14695981039346656037ULL;
    size_t length = wcslen(root);
    while (length && (root[length - 1] == L'\\' || root[length - 1] == L'/')) --length;
    for (size_t i = 0; i < length; ++i) {
        wchar_t c = root[i]; if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
        identity = (identity ^ (unsigned short)c) * 1099511628211ULL;
    }
    wchar_t name[96], className[96];
    StringCchPrintfW(className, ARRAYSIZE(className), L"FindIt.Startup.%016llx", identity);
    StringCchPrintfW(name, ARRAYSIZE(name), L"Local\\%s", className);
    HANDLE guard = CreateMutexW(NULL, FALSE, name);
    if (!guard) return 1;
    DWORD wait = WaitForSingleObject(guard, 0);
    if (wait == WAIT_TIMEOUT) {
        // Focus the first launch even during a long copy. Its UI message pump
        // stays responsive because all drive work runs on a separate thread.
        for (int attempt = 0; attempt < 20; ++attempt) {
            HWND first = FindWindowW(className, NULL);
            if (first) { activate(first); break; }
            Sleep(25);
        }
        CloseHandle(guard); return 0;
    }
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) { CloseHandle(guard); return 1; }

    INITCOMMONCONTROLSEX controls = {sizeof controls, ICC_PROGRESS_CLASS}; InitCommonControlsEx(&controls);
    WNDCLASSW wc = {0}; wc.lpfnWndProc = window_proc; wc.hInstance = instance;
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW); wc.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(IDI_FINDIT));
    wc.hbrBackground = GetSysColorBrush(COLOR_WINDOW); wc.lpszClassName = className;
    RegisterClassW(&wc);
    HDC screen = GetDC(NULL); int dpi = GetDeviceCaps(screen, LOGPIXELSX); ReleaseDC(NULL, screen);
    #define PX(value) MulDiv((value), dpi, 96)
    RECT rect = {0, 0, PX(500), PX(230)};
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    AdjustWindowRectEx(&rect, style, FALSE, 0);
    RECT work; SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    window = CreateWindowExW(0, className, L"FindIt", style, work.left + (work.right - work.left - rect.right + rect.left) / 2,
        work.top + (work.bottom - work.top - rect.bottom + rect.top) / 2, rect.right - rect.left, rect.bottom - rect.top, NULL, NULL, instance, NULL);
    if (!window) { ReleaseMutex(guard); CloseHandle(guard); return 1; }
    HFONT font = CreateFontW(-PX(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
    HFONT titleFont = CreateFontW(-PX(20), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
    HWND icon = CreateWindowW(L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_ICON, PX(24), PX(25), PX(32), PX(32), window, NULL, instance, NULL);
    HICON largeIcon = (HICON)LoadImageW(instance, MAKEINTRESOURCEW(IDI_FINDIT), IMAGE_ICON, PX(32), PX(32), LR_DEFAULTCOLOR);
    SendMessageW(icon, STM_SETICON, (WPARAM)largeIcon, 0);
    heading = CreateWindowW(L"STATIC", L"Starting FindIt...", WS_CHILD | WS_VISIBLE | SS_LEFT, PX(72), PX(25), PX(410), PX(52), window, NULL, instance, NULL);
    description = CreateWindowW(L"STATIC", L"Keep your drive connected.", WS_CHILD | WS_VISIBLE | SS_LEFT, PX(24), PX(90), PX(452), PX(40), window, NULL, instance, NULL);
    progress = CreateWindowW(PROGRESS_CLASSW, L"Startup progress", WS_CHILD | WS_VISIBLE | (animate ? PBS_MARQUEE : 0), PX(24), PX(143), PX(452), PX(10), window, NULL, instance, NULL);
    cancelButton = CreateWindowW(L"BUTTON", L"Cancel", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, PX(386), PX(180), PX(90), PX(30), window, (HMENU)IDCANCEL, instance, NULL);
    SendMessageW(heading, WM_SETFONT, (WPARAM)titleFont, TRUE);
    SendMessageW(description, WM_SETFONT, (WPARAM)font, TRUE); SendMessageW(cancelButton, WM_SETFONT, (WPARAM)font, TRUE);
    SendMessageW(progress, PBM_SETRANGE32, 0, 1000); SendMessageW(progress, PBM_SETMARQUEE, animate, 40);
    ShowWindow(window, SW_SHOW); UpdateWindow(window); SetFocus(cancelButton); SetTimer(window, 1, 100, NULL);

    // Show feedback before touching the version file, cache or application.
    HANDLE thread = NULL;
    DWORD localLength = GetEnvironmentVariableW(L"LOCALAPPDATA", local, CAP);
    wchar_t instances[CAP], relative[96];
    StringCchPrintfW(relative, ARRAYSIZE(relative), L"%016llx.pid", identity);
    if (!localLength || localLength >= CAP || !join(instances, local, L"FindIt\\instances") || !directories(instances) || !join(instancePath, instances, relative))
        failure(L"FindIt could not create its startup state on this PC. Check your Windows profile has free space and try again.");
    else thread = CreateThread(NULL, 0, launch, NULL, 0, NULL);
    int result = 1;
    if (!thread && !problem[0]) failure(L"Windows could not start FindIt. Try opening it again.");
    while (thread) {
        if (WaitForSingleObject(thread, 0) == WAIT_OBJECT_0) {
            DWORD code; GetExitCodeThread(thread, &code); result = (int)code;
            if (code || !child) break;
            HWND mainWindow = app_window(childPid);
            if (mainWindow) { activate(mainWindow); result = 0; break; }
            if (WaitForSingleObject(child, 0) == WAIT_OBJECT_0) {
                DWORD childCode = 0; GetExitCodeProcess(child, &childCode);
                result = childCode ? 1 : 0;
                if (result) failure(L"FindIt closed before its window appeared. Keep your drive connected and try again.");
                break;
            }
        }
        MsgWaitForMultipleObjects(0, NULL, FALSE, 50, QS_ALLINPUT);
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) { InterlockedExchange(&cancelled, 1); }
            if (!IsDialogMessageW(window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
        }
    }
    if (thread) CloseHandle(thread);
    if (child) CloseHandle(child);
    KillTimer(window, 1);
    if (result && !problem[0] && !cancelled) failure(L"FindIt could not finish opening. Keep your drive connected and try again.");
    if (problem[0] && !cancelled) MessageBoxW(window, problem, L"FindIt", MB_OK | MB_ICONWARNING);
    DestroyWindow(window); DeleteObject(font); DeleteObject(titleFont); if (largeIcon) DestroyIcon(largeIcon);
    ReleaseMutex(guard); CloseHandle(guard);
    return cancelled ? 0 : result;
}
