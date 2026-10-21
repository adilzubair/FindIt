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

#define CAP 32768

static int join(wchar_t *out, const wchar_t *a, const wchar_t *b) {
    size_t length = wcslen(a);
    const wchar_t *separator = length && (a[length - 1] == L'\\' || a[length - 1] == L'/') ? L"" : L"\\";
    return SUCCEEDED(StringCchPrintfW(out, CAP, L"%s%s%s", a, separator, b));
}

static void failure(const wchar_t *message) {
    MessageBoxW(NULL, message, L"FindIt", MB_OK | MB_ICONWARNING);
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

static int copy_tree(const wchar_t *source, const wchar_t *target) {
    wchar_t pattern[CAP];
    if (!directories(target) || !join(pattern, source, L"*")) return 0;
    WIN32_FIND_DATAW entry; HANDLE enumeration = FindFirstFileW(pattern, &entry);
    if (enumeration == INVALID_HANDLE_VALUE) return 0;
    int ok = 1;
    do {
        if (!wcscmp(entry.cFileName, L".") || !wcscmp(entry.cFileName, L"..")) continue;
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) { ok = 0; break; }
        wchar_t from[CAP], to[CAP];
        if (!join(from, source, entry.cFileName) || !join(to, target, entry.cFileName)) { ok = 0; break; }
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ok = copy_tree(from, to);
        else ok = CopyFileW(from, to, FALSE);
        if (!ok) break;
    } while (FindNextFileW(enumeration, &entry));
    FindClose(enumeration); return ok;
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
    for (char *line = text; ok && *line;) {
        char *end = strchr(line, '\n'); if (end) *end = 0;
        size_t length = strlen(line); if (length && line[length - 1] == '\r') line[--length] = 0;
        if (length) {
            if (length < 67 || line[64] != ' ' || line[65] != ' ') { ok = 0; break; }
            wchar_t relative[CAP], expected[65], actual[65], path[CAP];
            if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, line + 66, -1, relative, CAP)) { ok = 0; break; }
            // Only bundle-relative paths; neither a manifest nor a junction may redirect runtime caching.
            if (relative[0] == L'\\' || relative[0] == L'/' || wcschr(relative, L':') || wcsstr(relative, L"..")) { ok = 0; break; }
            for (int i = 0; i < 64; ++i) expected[i] = (wchar_t)line[i]; expected[64] = 0;
            if (!join(path, folder, relative) || !digest(path, actual) || _wcsicmp(expected, actual)) { ok = 0; break; }
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

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show) {
    SetCurrentProcessExplicitAppUserModelID(L"FindIt.Desktop");
    (void)instance; (void)previous; (void)commandLine; (void)show;
    wchar_t root[CAP], versionPath[CAP], version[65], source[CAP], cache[CAP], manifest[CAP], hash[65], app[CAP], complete[CAP];
    DWORD moduleLength = GetModuleFileNameW(NULL, root, CAP);
    if (!moduleLength || moduleLength >= CAP) return 1;
    wchar_t *slash = wcsrchr(root, L'\\'); if (!slash) return 1;
    // Preserve a drive root's slash: D: depends on a caller's per-drive current
    // directory, whereas D:\ always identifies the archive beside this launcher.
    if (slash > root && slash[-1] == L':') slash[1] = 0;
    else *slash = 0;
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
    wchar_t local[CAP];
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", local, CAP)) { failure(L"Windows did not provide a temporary application cache location."); return 1; }
    StringCchPrintfW(relative, 128, L"FindIt\\runtime\\%s-%.16s", version, hash);
    if (!join(cache, local, relative) || !join(complete, cache, L".complete")) return 1;
    if (GetFileAttributesW(complete) == INVALID_FILE_ATTRIBUTES) {
        if (!copy_tree(source, cache) || !verify_manifest(cache)) { failure(L"FindIt could not verify its application runtime. Keep the drive connected and make sure this PC has free space, then try again."); return 1; }
        HANDLE marker = CreateFileW(complete, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
        if (marker == INVALID_HANDLE_VALUE) return 1; CloseHandle(marker);
    }
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
    if (!CreateProcessW(app, arguments, NULL, NULL, FALSE, 0, NULL, cache, &startup, &process)) { failure(L"FindIt could not start. Restore the complete portable application package."); return 1; }
    CloseHandle(process.hThread); CloseHandle(process.hProcess); return 0;
}
