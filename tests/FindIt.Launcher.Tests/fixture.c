#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>

static wchar_t ready[32768];
static LRESULT CALLBACK fixture_proc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_TIMER && GetFileAttributesW(ready) != INVALID_FILE_ATTRIBUTES) {
        ShowWindow(window, SW_SHOW); KillTimer(window, 1);
    }
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR line, int show) {
    (void)previous; (void)line; (void)show;
    int argc; LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    const wchar_t *root = NULL;
    for (int i = 1; i + 1 < argc; ++i) if (!wcscmp(argv[i], L"--root")) { root = argv[i + 1]; break; }
    if (!root) return 21;
    wchar_t path[32768];
    StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\launches.txt", root);
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 22;
    DWORD written; WriteFile(file, "launch\r\n", 8, &written, NULL); CloseHandle(file);
    StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\arguments.txt", root);
    file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 23;
    WriteFile(file, GetCommandLineW(), (DWORD)(wcslen(GetCommandLineW()) * sizeof(wchar_t)), &written, NULL); CloseHandle(file);
    StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\exit-immediately", root);
    if (GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES) return 24;
    StringCchPrintfW(ready, ARRAYSIZE(ready), L"%s\\show-window", root);
    LocalFree(argv);
    WNDCLASSW type = {0}; type.lpfnWndProc = fixture_proc; type.hInstance = instance;
    type.lpszClassName = L"FindIt.LauncherFixture"; type.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    RegisterClassW(&type);
    HWND window = CreateWindowW(type.lpszClassName, L"FindIt launcher test library", WS_OVERLAPPEDWINDOW,
        120, 120, 640, 360, NULL, NULL, instance, NULL);
    SetTimer(window, 1, 50, NULL);
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
