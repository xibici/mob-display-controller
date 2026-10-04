// start_menu_size_hook.cpp
//
// Sizes the Windows 11 Start menu from this app: the DLL is injected into the process that hosts the
// menu (SearchHost.exe) and changes the size of its XAML elements from the inside.
//
// The XAML part is ported from third_party/windhawk-mods/start-menu-size.wh.cpp (GPL-3.0, by m417z).
// Inside that process the Start menu's tree is reachable through Windows::UI::Xaml::Window::Current(),
// and for the redesigned menu the size lives on
//     FrameRoot                                -- height (and margin)
//     FrameRoot > AnimationRoot > MainMenu     -- width
//
// Windhawk provided two things that had to be replaced here:
//   - the settings, which now come from a file in ProgramData so this app can change them at any time;
//   - the log, which goes to a file there too, because the host is an AppContainer and cannot write
//     anywhere else (the deployment script grants the ACL that allows it).
//
// The work runs on the Start menu's own UI thread: XAML objects may only be touched from the thread that
// owns them, and this DLL is entered on an injected thread, so it is posted there with the same
// WH_CALLWNDPROC trick the Windhawk mods use (RunFromWindowThread).

#include <windows.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>

#include <cmath>
#include <functional>
#include <optional>

using namespace winrt::Windows::UI::Xaml;
using namespace winrt::Windows::UI::Xaml::Media;

namespace {

const wchar_t* kDirectory = L"C:\\ProgramData\\MobDisplayController";
const wchar_t* kSettingsPath = L"C:\\ProgramData\\MobDisplayController\\start-menu-size.ini";
const wchar_t* kLogPath = L"C:\\ProgramData\\MobDisplayController\\start-menu-size.log";

struct Settings
{
    int width = 0;
    int height = 0;
};

Settings g_settings;
bool g_applyPending;
winrt::event_token g_visibilityToken;
winrt::event_token g_layoutToken;

void Log(const wchar_t* format, ...)
{
    wchar_t message[512];
    va_list args;
    va_start(args, format);
    wvsprintfW(message, format, args);
    va_end(args);

    HANDLE file = CreateFileW(kLogPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return;

    SYSTEMTIME now;
    GetLocalTime(&now);

    wchar_t line[600];
    wsprintfW(line, L"%04d-%02d-%02d %02d:%02d:%02d  %s\r\n", now.wYear, now.wMonth, now.wDay,
              now.wHour, now.wMinute, now.wSecond, message);

    DWORD written = 0;
    WriteFile(file, line, static_cast<DWORD>(lstrlenW(line) * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(file);
}

int ReadInt(const char* text, const char* key, int fallback)
{
    const char* at = strstr(text, key);
    if (at == nullptr)
        return fallback;

    at += lstrlenA(key);
    while (*at == ' ' || *at == '=' || *at == ':')
        ++at;

    return atoi(at);
}

void ReadSettings()
{
    if (GetFileAttributesW(kSettingsPath) == INVALID_FILE_ATTRIBUTES)
        return;

    HANDLE file = CreateFileW(kSettingsPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        Log(L"settings file exists but could not be opened: %lu", GetLastError());
        return;
    }

    char buffer[256] = {};
    DWORD read = 0;
    ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);

    g_settings.width = ReadInt(buffer, "width", 0);
    g_settings.height = ReadInt(buffer, "height", 0);
}

FrameworkElement EnumChildElements(FrameworkElement const& element,
                                   std::function<bool(FrameworkElement const&)> const& callback)
{
    int childrenCount = VisualTreeHelper::GetChildrenCount(element);
    for (int i = 0; i < childrenCount; i++)
    {
        auto child = VisualTreeHelper::GetChild(element, i).try_as<FrameworkElement>();
        if (!child)
            continue;

        if (callback(child))
            return child;
    }

    return nullptr;
}

FrameworkElement FindChildByName(FrameworkElement const& element, const wchar_t* name)
{
    return EnumChildElements(element, [name](FrameworkElement const& child) {
        return child.Name() == name;
    });
}

FrameworkElement FindChildByClassName(FrameworkElement const& element, const wchar_t* className)
{
    return EnumChildElements(element, [className](FrameworkElement const& child) {
        return winrt::get_class_name(child) == className;
    });
}

struct OriginalWidth
{
    std::optional<double> width;
    std::optional<double> minWidth;
    std::optional<double> maxWidth;
};

struct OriginalHeight
{
    std::optional<double> height;
    std::optional<double> minHeight;
    std::optional<double> maxHeight;
};

std::optional<OriginalWidth> g_originalMainMenuWidth;
std::optional<OriginalHeight> g_originalFrameRootHeight;
std::optional<Thickness> g_originalFrameRootMargin;

void SaveWidth(FrameworkElement const& element, OriginalWidth& out)
{
    double width = element.Width();
    out.width = std::isnan(width) ? std::nullopt : std::optional(width);

    double minWidth = element.MinWidth();
    out.minWidth = minWidth == 0 ? std::nullopt : std::optional(minWidth);

    double maxWidth = element.MaxWidth();
    out.maxWidth = std::isinf(maxWidth) ? std::nullopt : std::optional(maxWidth);
}

void RestoreWidth(FrameworkElement const& element, OriginalWidth const& original)
{
    auto dependencyObject = element.as<DependencyObject>();

    if (original.width)
        element.Width(*original.width);
    else
        dependencyObject.ClearValue(FrameworkElement::WidthProperty());

    if (original.minWidth)
        element.MinWidth(*original.minWidth);
    else
        dependencyObject.ClearValue(FrameworkElement::MinWidthProperty());

    if (original.maxWidth)
        element.MaxWidth(*original.maxWidth);
    else
        dependencyObject.ClearValue(FrameworkElement::MaxWidthProperty());
}

void SaveHeight(FrameworkElement const& element, OriginalHeight& out)
{
    double height = element.Height();
    out.height = std::isnan(height) ? std::nullopt : std::optional(height);

    double minHeight = element.MinHeight();
    out.minHeight = minHeight == 0 ? std::nullopt : std::optional(minHeight);

    double maxHeight = element.MaxHeight();
    out.maxHeight = std::isinf(maxHeight) ? std::nullopt : std::optional(maxHeight);
}

void RestoreHeight(FrameworkElement const& element, OriginalHeight const& original)
{
    auto dependencyObject = element.as<DependencyObject>();

    if (original.height)
        element.Height(*original.height);
    else
        dependencyObject.ClearValue(FrameworkElement::HeightProperty());

    if (original.minHeight)
        element.MinHeight(*original.minHeight);
    else
        dependencyObject.ClearValue(FrameworkElement::MinHeightProperty());

    if (original.maxHeight)
        element.MaxHeight(*original.maxHeight);
    else
        dependencyObject.ClearValue(FrameworkElement::MaxHeightProperty());
}

void ApplyStyleRedesignedStartMenu(FrameworkElement const& content)
{
    FrameworkElement frameRoot = FindChildByName(content, L"FrameRoot");
    if (!frameRoot)
    {
        Log(L"FrameRoot not found");
        return;
    }

    FrameworkElement animationRoot = FindChildByName(frameRoot, L"AnimationRoot");
    if (!animationRoot)
    {
        Log(L"AnimationRoot not found");
        return;
    }

    FrameworkElement mainMenu = FindChildByName(animationRoot, L"MainMenu");
    if (!mainMenu)
    {
        Log(L"MainMenu not found");
        return;
    }

    Log(L"applying %dx%d", g_settings.width, g_settings.height);

    // The floor is where the Start menu survives, and it applies to the *inner* width (MainMenu), so the
    // narrowest panel this can produce is about 324 DIP visible (measured 488 px at 150%).
    // Measured on this machine (Windows 11 24H2, 1920x1200 at 150%): 276 inner (300 visible) draws fine,
    // 256 inner (280 visible) stops the panel drawing at all - and it then stays broken for every later
    // open, even with the setting put back; only restarting SearchHost / StartMenuExperienceHost recovers
    // it. Hence 300 here, and 350 as the narrowest the app's menu offers (daylight above the boundary).
    // (The Windhawk mod uses 270, which is inside this machine's broken range.)
    constexpr int kMinWidth = 300;

    if (g_settings.width > 0)
    {
        if (!g_originalMainMenuWidth)
        {
            g_originalMainMenuWidth.emplace();
            SaveWidth(mainMenu, *g_originalMainMenuWidth);
        }

        // The requested width is the visible width; MainMenu sits inside FrameRoot > AnimationRoot, so
        // whatever those add around it has to come off first.
        double padding = 0;
        if (frameRoot.ActualWidth() > 0 && mainMenu.ActualWidth() > 0)
            padding = frameRoot.ActualWidth() - mainMenu.ActualWidth();

        double width = fmax(static_cast<double>(g_settings.width), kMinWidth);
        width = fmax(width - padding, kMinWidth);

        mainMenu.Width(width);
        mainMenu.MinWidth(width);
        mainMenu.MaxWidth(width);
    }
    else if (g_originalMainMenuWidth)
    {
        RestoreWidth(mainMenu, *g_originalMainMenuWidth);
        g_originalMainMenuWidth.reset();
    }

    if (g_settings.height > 0)
    {
        if (!g_originalFrameRootHeight)
        {
            g_originalFrameRootHeight.emplace();
            SaveHeight(frameRoot, *g_originalFrameRootHeight);
        }

        double height = static_cast<double>(g_settings.height);
        frameRoot.Height(height);
        frameRoot.MinHeight(height);
        frameRoot.MaxHeight(height);

        if (!g_originalFrameRootMargin)
            g_originalFrameRootMargin = frameRoot.Margin();
        frameRoot.Margin(Thickness{0, 0, 0, 0});
    }
    else if (g_originalFrameRootHeight)
    {
        RestoreHeight(frameRoot, *g_originalFrameRootHeight);
        g_originalFrameRootHeight.reset();

        if (g_originalFrameRootMargin)
        {
            frameRoot.Margin(*g_originalFrameRootMargin);
            g_originalFrameRootMargin.reset();
        }
    }
}

void ApplyStyleClassicStartMenu(FrameworkElement const& content)
{
    FrameworkElement startSizingFrame = FindChildByClassName(content, L"StartDocked.StartSizingFrame");
    if (!startSizingFrame)
    {
        Log(L"StartDocked.StartSizingFrame not found");
        return;
    }

    // Only the redesigned menu's size is applied; the classic layout is left as it is.
    Log(L"classic Start menu content, left alone");
}

void ApplyStyle()
{
    ReadSettings();

    auto window = Window::Current();
    if (!window)
    {
        Log(L"Window::Current() is null");
        return;
    }

    auto content = window.Content().try_as<FrameworkElement>();
    if (!content)
    {
        Log(L"the XAML window has no FrameworkElement content");
        return;
    }

    winrt::hstring className = winrt::get_class_name(content);
    if (className == L"StartMenu.StartBlendedFlexFrame")
        ApplyStyleRedesignedStartMenu(content);
    else if (className == L"Windows.UI.Xaml.Controls.Canvas")
        ApplyStyleClassicStartMenu(content);
    else
        Log(L"unsupported Start menu content class: %s", className.c_str());
}

void OnLayoutUpdated(winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&)
{
    if (!g_applyPending)
        return;

    g_applyPending = false;

    try
    {
        ApplyStyle();
    }
    catch (winrt::hresult_error const& error)
    {
        Log(L"ApplyStyle failed: 0x%08X", static_cast<unsigned>(error.code().value));
    }
}

// Runs on the Start menu's UI thread - everything XAML has to be touched from there.
void InitOnWindowThread(void*)
{
    try
    {
        auto window = Window::Current();
        if (!window)
        {
            Log(L"Window::Current() is null on the window thread");
            return;
        }

        g_visibilityToken = window.VisibilityChanged([](auto&&, auto&&) { g_applyPending = true; });

        auto content = window.Content().try_as<FrameworkElement>();
        if (content)
            g_layoutToken = content.LayoutUpdated(&OnLayoutUpdated);

        Log(L"attached to the Start menu window");
        ApplyStyle();
    }
    catch (winrt::hresult_error const& error)
    {
        Log(L"Init failed: 0x%08X", static_cast<unsigned>(error.code().value));
    }
}

using RunFromWindowThreadProc = void(WINAPI*)(void*);

bool RunFromWindowThread(HWND window, RunFromWindowThreadProc proc, void* parameter)
{
    static const UINT runFromWindowThreadMessage =
        RegisterWindowMessageW(L"MobDisplayController_RunFromWindowThread");

    DWORD threadId = GetWindowThreadProcessId(window, nullptr);
    if (threadId == 0)
        return false;

    if (threadId == GetCurrentThreadId())
    {
        proc(parameter);
        return true;
    }

    struct Parameters
    {
        RunFromWindowThreadProc proc;
        void* parameter;
    };

    // A WH_CALLWNDPROC hook is installed for the target thread so the message below is delivered *on*
    // that thread, where the XAML objects are valid.
    HHOOK hook = SetWindowsHookExW(
        WH_CALLWNDPROC,
        [](int code, WPARAM wParam, LPARAM lParam) -> LRESULT {
            if (code == HC_ACTION)
            {
                const CWPSTRUCT* message = reinterpret_cast<const CWPSTRUCT*>(lParam);
                if (message->message == runFromWindowThreadMessage)
                {
                    auto parameters = reinterpret_cast<Parameters*>(message->lParam);
                    parameters->proc(parameters->parameter);
                }
            }

            return CallNextHookEx(nullptr, code, wParam, lParam);
        },
        nullptr, threadId);

    if (!hook)
    {
        Log(L"SetWindowsHookEx(WH_CALLWNDPROC) failed: %lu", GetLastError());
        return false;
    }

    Parameters parameters{proc, parameter};
    SendMessageW(window, runFromWindowThreadMessage, 0, reinterpret_cast<LPARAM>(&parameters));
    UnhookWindowsHookEx(hook);
    return true;
}

/// The window the Start menu draws into, in this process.
HWND FindCoreWindow()
{
    HWND found = nullptr;
    EnumWindows(
        [](HWND window, LPARAM parameter) -> BOOL {
            DWORD processId = 0;
            if (!GetWindowThreadProcessId(window, &processId) || processId != GetCurrentProcessId())
                return TRUE;

            wchar_t className[64];
            if (GetClassNameW(window, className, ARRAYSIZE(className)) == 0)
                return TRUE;

            if (_wcsicmp(className, L"Windows.UI.Core.CoreWindow") == 0)
            {
                *reinterpret_cast<HWND*>(parameter) = window;
                return FALSE;
            }

            return TRUE;
        },
        reinterpret_cast<LPARAM>(&found));

    return found;
}

DWORD WINAPI WorkerThread(void*)
{
    CreateDirectoryW(kDirectory, nullptr);
    Log(L"worker started (pid %lu)", GetCurrentProcessId());

    for (int attempt = 0; attempt < 60; attempt++)
    {
        HWND window = FindCoreWindow();
        if (window != nullptr && RunFromWindowThread(window, InitOnWindowThread, nullptr))
            return 0;

        Sleep(1000);
    }

    Log(L"gave up: no Start menu window found in this process");
    return 0;
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);

        // Never do real work here - the loader lock is held: hand it to a thread of our own.
        HANDLE thread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
        if (thread != nullptr)
            CloseHandle(thread);
    }

    return TRUE;
}
