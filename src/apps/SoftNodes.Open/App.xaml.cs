using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FactoryNodes.Localization;

namespace SoftNodesOpen;

/// <summary>
/// La aplicación: pone el idioma guardado (o el de Windows) y abre la ventana sobre la carpeta de las instancias (<c>--instancias &lt;ruta&gt;</c> o
/// Documentos\SoftNodes\instancias, la misma que SoftNodes Pro). Un error no controlado se enseña y se sigue: un PLC simulado no se cierra por un
/// fallo de pintado.
/// </summary>
public partial class App : Application
{
    public const string WaitArgument = "--esperar";

    protected override void OnStartup(StartupEventArgs e)
    {
        WaitForPrevious(e.Args);
        var at = Array.FindIndex(e.Args, a => a.Equals("--instancias", StringComparison.OrdinalIgnoreCase));
        var root = at >= 0 && at + 1 < e.Args.Length ? Path.GetFullPath(e.Args[at + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SoftNodes", "instancias");
        Directory.CreateDirectory(root);
        var services = new WpfServices(root);
        var language = AppLanguages.Resolve(services.SavedLanguage, services.SystemCulture);
        System.Globalization.CultureInfo.CurrentUICulture = language.Culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = language.Culture;
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        var window = new MainWindow(services);
        MainWindow = window;
        window.Show();
    }

    private static void WaitForPrevious(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals(WaitArgument, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out var pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(15000);
        }
        catch (ArgumentException)
        {
            // ya había terminado
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, FactoryNodes.SoftNodes.Open.Presentation.SoftNodesOpenViewModel.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
