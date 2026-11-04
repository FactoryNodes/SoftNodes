using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FactoryNodes.Localization;
using FactoryNodes.SoftNodes.Instances;

namespace FactoryNodes.SoftNodes.Open.Presentation;

/// <summary>Los textos del SoftNodes abierto en el idioma elegido: <c>L.T("Nueva instancia…")</c>; la clave es el castellano.</summary>
public static class L
{
    private static readonly Texts Strings = Texts.For<SoftNodesOpenViewModel>("FactoryNodes.SoftNodes.Open.Presentation.Localization.Strings");

    public static string T(string spanish) => Strings.T(spanish);

    public static string F(string spanishFormat, params object?[] args) => Strings.F(spanishFormat, args);
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null) : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Un idioma en la lista del botón del globo.</summary>
public sealed class LanguageChoice(AppLanguage language, bool isCurrent, Action<AppLanguage> choose)
{
    public string Name => language.NativeName;
    public bool IsCurrent { get; } = isCurrent;
    public ICommand ChooseCommand { get; } = new RelayCommand(() => choose(language));
}

/// <summary>Los dibujos (SVG 24×24) de los botones, incrustados en la DLL: los mismos que SoftNodes Pro.</summary>
public static class OpenIcons
{
    public static string NewInstance => Get("plc-add");
    public static string RemoveInstance => Get("plc-remove");
    public static string Language => Get("language");
    public static string PowerOn => Get("power-on");
    public static string PowerOff => Get("power-off");
    public static string Run => Get("run");
    public static string Stop => Get("stop");
    public static string Load => Get("load");
    public static string Reload => Get("reload");
    public static string ColdStart => Get("cold-start");
    public static string Pause => Get("pause");
    public static string StopCycle => Get("stop-cycle");
    public static string StepOver => Get("step-over");
    public static string StepInto => Get("step-into");
    public static string StepOut => Get("step-out");
    public static string Cycle => Get("cycle");
    public static string RunToCursor => Get("run-to-cursor");
    public static string Breakpoint => Get("breakpoint");

    private static readonly Assembly Assembly = typeof(OpenIcons).Assembly;
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Get(string name) => Cache.GetOrAdd(name, n =>
    {
        using var stream = Assembly.GetManifestResourceStream("Icons/" + n + ".svg") ?? throw new InvalidOperationException($"No hay icono «{n}».");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}

/// <summary>Lo que el ViewModel pide a la app: la carpeta de las instancias, elegir un fichero, preguntar, el hilo de la ventana y el idioma.</summary>
public interface IOpenSoftNodesServices
{
    IInstanceStore Store { get; }

    /// <summary>El cliente para leer a un PLC real por los enlaces entre autómatas (EtherNet/IP); null = solo entre instancias abiertas.</summary>
    Func<FactoryNodes.Iec61131.Emission.IPlcAddressing, FactoryNodes.Monitoring.IPlcConnection>? PlcClients { get; }

    /// <summary>Elegir un programa <c>.softnodes</c>; devuelve sus bytes y el nombre del fichero, o null si se cancela.</summary>
    (byte[] Bytes, string FileName)? ChooseProgram();

    bool Confirm(string message);

    /// <summary>Lo que llega de otros hilos (el registro de los servidores), al hilo de la ventana.</summary>
    void OnUiThread(Action action);

    string? SavedLanguage { get; set; }

    System.Globalization.CultureInfo SystemCulture { get; }

    void Restart();
}
