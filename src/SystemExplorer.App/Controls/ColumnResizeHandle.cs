using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace SystemExplorer.App.Controls;

public sealed class ColumnResizeHandle : Button
{
    public ProcessColumns? Columns { get; set; }
    public int ColumnIndex { get; set; }
    private double startX;
    private double startWidth;
    private uint? pointerId;

    public ColumnResizeHandle()
    {
        Content = "│";
        Width = 12;
        MinWidth = 12;
        Padding = new Thickness(0);
        BorderThickness = new Thickness(0);
        Background = null;
        HorizontalAlignment = HorizontalAlignment.Right;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        ToolTipService.SetToolTip(this, "Drag to resize. Keyboard: Left / Right, Home / End.");
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (Columns is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { base.OnPointerPressed(e); return; }
        Focus(FocusState.Pointer);
        startX = e.GetCurrentPoint(null).Position.X;
        startWidth = Columns.GetWidth(ColumnIndex);
        if (CapturePointer(e.Pointer)) pointerId = e.Pointer.PointerId;
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (pointerId == e.Pointer.PointerId && Columns is not null)
        {
            Columns.Resize(ColumnIndex, startWidth + e.GetCurrentPoint(null).Position.X - startX);
            e.Handled = true;
        }
        else base.OnPointerMoved(e);
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (pointerId == e.Pointer.PointerId) { ReleasePointerCapture(e.Pointer); pointerId = null; e.Handled = true; }
        else base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) { pointerId = null; base.OnPointerCaptureLost(e); }
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (Columns is not null && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End)
        {
            var width = e.Key switch { VirtualKey.Left => Columns.GetWidth(ColumnIndex) - 16, VirtualKey.Right => Columns.GetWidth(ColumnIndex) + 16, VirtualKey.Home => 0, _ => 1000 };
            Columns.Resize(ColumnIndex, width);
            e.Handled = true;
        }
        else base.OnKeyDown(e);
    }
}
