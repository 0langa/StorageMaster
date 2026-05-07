using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StorageMaster.UI.Controls;

public sealed partial class RadialGauge : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RadialGauge), new PropertyMetadata(0d, OnGaugeChanged));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(RadialGauge), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GaugeSizeProperty =
        DependencyProperty.Register(nameof(GaugeSize), typeof(double), typeof(RadialGauge), new PropertyMetadata(92d));

    public RadialGauge()
    {
        InitializeComponent();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double GaugeSize
    {
        get => (double)GetValue(GaugeSizeProperty);
        set => SetValue(GaugeSizeProperty, value);
    }

    public string ValueText => $"{Math.Clamp(Value, 0, 100):N0}%";

    private static void OnGaugeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RadialGauge gauge)
            gauge.Bindings.Update();
    }
}
