// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// AdwClamp: constrains its child to <see cref="MaximumSize"/> and centres it.
/// Between <see cref="TighteningThreshold"/> and the maximum the child width
/// follows libadwaita's ease-out-cubic curve, so the margins grow smoothly.
/// </summary>
public sealed partial class Clamp : Panel
{
    public static readonly DependencyProperty MaximumSizeProperty = DependencyProperty.Register(
        nameof(MaximumSize), typeof(double), typeof(Clamp), new PropertyMetadata(600.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty TighteningThresholdProperty = DependencyProperty.Register(
        nameof(TighteningThreshold), typeof(double), typeof(Clamp), new PropertyMetadata(400.0, OnLayoutPropertyChanged));

    public double MaximumSize
    {
        get => (double)GetValue(MaximumSizeProperty);
        set => SetValue(MaximumSizeProperty, value);
    }

    public double TighteningThreshold
    {
        get => (double)GetValue(TighteningThresholdProperty);
        set => SetValue(TighteningThresholdProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Clamp)d).InvalidateMeasure();

    /// <summary>The width given to the child for an available width (adw-clamp.c).</summary>
    public static double ChildWidth(double available, double maximum, double threshold)
    {
        if (double.IsInfinity(available))
            return maximum;

        double lower = Math.Min(threshold, maximum);
        double upper = lower + 3 * (maximum - lower);
        if (available <= lower)
            return available;
        if (available >= upper)
            return maximum;

        double progress = (available - lower) / (upper - lower);
        return lower + (maximum - lower) * (1 - Math.Pow(1 - progress, 3));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = ChildWidth(availableSize.Width, MaximumSize, TighteningThreshold);
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = ChildWidth(finalSize.Width, MaximumSize, TighteningThreshold);
        double x = Math.Max(0, (finalSize.Width - width) / 2);
        foreach (var child in Children)
            child.Arrange(new Rect(x, 0, width, finalSize.Height));

        return finalSize;
    }
}
