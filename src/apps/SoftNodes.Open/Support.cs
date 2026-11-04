using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using FactoryNodes.SoftNodes.Instances;
using FactoryNodes.SoftNodes.Open.Presentation;
using Microsoft.Win32;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace SoftNodesOpen;

/// <summary>Un texto fijo del XAML en el idioma del programa: <c>Content="{l:T 'Encender'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

/// <summary>Pinta un SVG (texto) como imagen WPF. Si no se puede dibujar, null.</summary>
public sealed class SvgTextToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string svg || string.IsNullOrWhiteSpace(svg)) return null;
        try
        {
            var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = false, OptimizePath = true };
            using var reader = new FileSvgReader(settings);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            var drawing = reader.Read(stream);
            if (drawing is null) return null;
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Las instancias en el disco: una carpeta cada una dentro de <see cref="Root"/> (la misma que SoftNodes Pro).</summary>
public sealed class DirectoryInstanceStore(string root) : IInstanceStore
{
    public string Root { get; } = root;

    public IReadOnlyList<string> ListInstances() =>
        !Directory.Exists(Root) ? [] : Directory.GetDirectories(Root).Where(d => File.Exists(Path.Combine(d, SimulatedPlc.SettingsFile))).Select(d => Path.GetFileName(d)!).ToList();

    public string? Read(string instance, string path) => File.Exists(FileOf(instance, path)) ? File.ReadAllText(FileOf(instance, path)) : null;

    public void Write(string instance, string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf(instance, path))!);
        File.WriteAllText(FileOf(instance, path), text);
    }

    public byte[]? ReadBytes(string instance, string path) => File.Exists(FileOf(instance, path)) ? File.ReadAllBytes(FileOf(instance, path)) : null;

    public void WriteBytes(string instance, string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf(instance, path))!);
        File.WriteAllBytes(FileOf(instance, path), content);
    }

    public DateTime? LastWrite(string instance, string path) => File.Exists(FileOf(instance, path)) ? File.GetLastWriteTime(FileOf(instance, path)) : null;

    public void DeleteInstance(string instance)
    {
        var folder = Path.Combine(Root, instance);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    public string PkiFolder(string instance) => Path.Combine(Root, instance, "pki");

    private string FileOf(string instance, string path) => Path.Combine(Root, instance, path.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Lo que el ViewModel pide a Windows: la carpeta, el diálogo de abrir, preguntar, el hilo de la ventana y el idioma.</summary>
public sealed class WpfServices(string root) : IOpenSoftNodesServices
{
    private static readonly string LanguageFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftNodes", "language-open.txt");

    public IInstanceStore Store { get; } = new DirectoryInstanceStore(root);

    /// <summary>Los enlaces con un PLC real van por EtherNet/IP (lo que lee el router: <c>eip://ip:44818</c>).</summary>
    public Func<FactoryNodes.Iec61131.Emission.IPlcAddressing, FactoryNodes.Monitoring.IPlcConnection>? PlcClients { get; } =
        addressing => new FactoryNodes.Monitoring.Cip.CipPlcConnection(addressing);

    public Window? Owner { get; set; }

    public (byte[] Bytes, string FileName)? ChooseProgram()
    {
        var dialog = new OpenFileDialog { Filter = SoftNodesOpenViewModel.ProgramFilter + "|*.softnodes" };
        return dialog.ShowDialog(Owner) == true ? (File.ReadAllBytes(dialog.FileName), Path.GetFileName(dialog.FileName)) : null;
    }

    public bool Confirm(string message) =>
        MessageBox.Show(Owner!, message, SoftNodesOpenViewModel.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public string? SavedLanguage
    {
        get => File.Exists(LanguageFile) ? File.ReadAllText(LanguageFile).Trim() : null;
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile)!);
            File.WriteAllText(LanguageFile, value ?? "");
        }
    }

    public CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    public void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(App.WaitArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(start);
        }

        Application.Current.MainWindow?.Close();
    }
}
