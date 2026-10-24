using System.ComponentModel;
using System.Windows;

namespace WpfBindingSample;

public sealed class ProbeViewModel : INotifyPropertyChanged
{
    private string _name = "";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly ProbeViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void OnSubmit(object sender, RoutedEventArgs e)
        => SubmitRead.Text = $"提交读到: {_vm.Name}";
}
