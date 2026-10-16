using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GitHubToDo;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
#if DEBUG
    public static bool IsDemo { get; private set; }
    public static string DemoProfile { get; private set; } = "";
#endif
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
#if DEBUG
        // Attach before resource initialization so startup failures retain their
        // managed exception details in the debugger capture. Never mark handled.
        UnhandledException += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[GitHubToDo] XAML unhandled exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[GitHubToDo] CLR unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
#endif
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
#if DEBUG
        // Desktop packaged activation can put explicit arguments on the process
        // command line while leaving XAML LaunchActivatedEventArgs.Arguments empty.
        // Require the actual --demo token from either documented launch surface;
        // never infer demo mode from credentials, configuration, or launch failure.
        var arguments = args.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(Environment.GetCommandLineArgs().Skip(1)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IsDemo = arguments.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var profile = arguments.SingleOrDefault(argument => argument.StartsWith("--demo-profile=", StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            if (!IsDemo || !Guid.TryParseExact(profile["--demo-profile=".Length..], "N", out var id))
                throw new ArgumentException("--demo-profile requires --demo and a GUID in N format.");
            DemoProfile = id.ToString("N");
        }
#endif
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}
