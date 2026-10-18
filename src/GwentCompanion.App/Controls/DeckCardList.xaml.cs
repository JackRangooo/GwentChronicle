using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GwentCompanion.App.Controls;

public partial class DeckCardList : UserControl
{
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(DeckCardList), new PropertyMetadata(38d));
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public static readonly DependencyProperty ShowDetailsProperty = DependencyProperty.Register(nameof(ShowDetails), typeof(bool), typeof(DeckCardList), new PropertyMetadata(true));
    public bool ShowDetails { get => (bool)GetValue(ShowDetailsProperty); set => SetValue(ShowDetailsProperty, value); }
    public static readonly DependencyProperty ShowPreviewActionProperty = DependencyProperty.Register(nameof(ShowPreviewAction), typeof(bool), typeof(DeckCardList), new PropertyMetadata(true));
    public static readonly DependencyProperty PreviewOnRowClickProperty=DependencyProperty.Register(nameof(PreviewOnRowClick),typeof(bool),typeof(DeckCardList),new PropertyMetadata(false));
    public bool PreviewOnRowClick {get=>(bool)GetValue(PreviewOnRowClickProperty);set=>SetValue(PreviewOnRowClickProperty,value);}
    public bool ShowPreviewAction { get => (bool)GetValue(ShowPreviewActionProperty); set => SetValue(ShowPreviewActionProperty, value); }
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(IEnumerable),
        typeof(DeckCardList), new PropertyMetadata(null));
    public IEnumerable? Rows { get => (IEnumerable?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public DeckCardList()
    {
        InitializeComponent();
        PreviewMouseWheel += CardListMouseWheel;
    }
    private void CardListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if(e.Delta==0)return;
        // A ListBox consumes wheel events even when its own ScrollViewer has no
        // overflow. Walk outwards until a viewer can move in this direction.
        for(var current=e.OriginalSource as DependencyObject;current is not null;current=ScrollAncestorParent(current))
        {
            if(current is not ScrollViewer viewer || viewer.ScrollableHeight<=0 ||
                (e.Delta<0 ? viewer.VerticalOffset>=viewer.ScrollableHeight-.01 : viewer.VerticalOffset<=.01))continue;
            var notches=Math.Max(1,(int)Math.Ceiling(Math.Abs(e.Delta)/120d));
            if(SystemParameters.WheelScrollLines<0)
            {
                for(var n=0;n<notches;n++){if(e.Delta<0)viewer.PageDown();else viewer.PageUp();}
            }
            else
            {
                var lines=SystemParameters.WheelScrollLines*notches;
                for(var n=0;n<lines;n++){if(e.Delta<0)viewer.LineDown();else viewer.LineUp();}
            }
            e.Handled=true;return;
        }
    }
    private static DependencyObject? ScrollAncestorParent(DependencyObject value) => value is FrameworkContentElement content ? content.Parent
        : value is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
    public event EventHandler<object>? RowActivated;
    public event EventHandler<object>? RowInspected;
    public event EventHandler<object>? RowPreviewRequested;
    public void HighlightRows(IEnumerable<object> rows)
    {
        CardRows.SelectionMode = SelectionMode.Multiple;
        CardRows.SelectedItems.Clear();
        foreach (var row in rows) CardRows.SelectedItems.Add(row);
    }
    private void Row_OnInspected(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: { } row }) RowInspected?.Invoke(this, row);
    }
    private void Row_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: { } row }) {
            if(PreviewOnRowClick) Preview(row); else RowActivated?.Invoke(this, row);
        }
    }
    private void Row_OnPreviewRequested(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: { } row }) Preview(row);
        e.Handled = true;
    }
    private void Preview(object row) {
        if(RowPreviewRequested is not null) RowPreviewRequested.Invoke(this,row);
        else if(row is ICardPreviewRow {Card:{ } card}) CardInspection.Show(this,card);
    }
}
