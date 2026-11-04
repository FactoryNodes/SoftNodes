using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using FactoryNodes.SoftNodes.Open.Presentation;
using SoftNodes.Controls;

namespace SoftNodesOpen;

/// <summary>Solo pegamento: el ViewModel, el reloj de dos veces por segundo, el menú del idioma y apagar las instancias al cerrar.</summary>
public partial class MainWindow : Window
{
    private readonly SoftNodesOpenViewModel _model;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DebugEditorAdapter _adapter;
    private bool _closing;

    public MainWindow(WpfServices services)
    {
        InitializeComponent();
        services.Owner = this;
        _model = new SoftNodesOpenViewModel(services);
        DataContext = _model;
        // El editor del depurador enseña el de la instancia elegida
        _adapter = new DebugEditorAdapter(Editor);
        _adapter.Bind(_model.Selected?.Debug);
        _model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SoftNodesOpenViewModel.Selected)) _adapter.Bind(_model.Selected?.Debug); };
        _timer.Tick += (_, _) => _model.Tick();
        _timer.Start();
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        LanguageButton.ContextMenu!.PlacementTarget = LanguageButton;
        LanguageButton.ContextMenu.DataContext = _model;
        LanguageButton.ContextMenu.IsOpen = true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        if (_model.Instances.Any(i => i.IsOn) &&
            MessageBox.Show(this, SoftNodesOpenViewModel.CloseQuestion, SoftNodesOpenViewModel.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Apagar con calma (guarda la retentiva y cierra los servidores) y luego cerrar de verdad
        e.Cancel = true;
        _closing = true;
        _timer.Stop();
        await _model.ShutdownAsync();
        Close();
    }
}
