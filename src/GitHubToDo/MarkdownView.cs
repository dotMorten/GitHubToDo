using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Common.Parsers.Markdown;
using CommunityToolkit.Common.Parsers.Markdown.Blocks;
using CommunityToolkit.Common.Parsers.Markdown.Render;
using CommunityToolkit.WinUI.UI.Controls;
using CommunityToolkit.WinUI.UI.Controls.Markdown.Render;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace GitHubToDo;

public sealed partial class MarkdownView : MarkdownTextBlock
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(TableRenderer))]
    public MarkdownView()
    {
        SetRenderer<TableRenderer>();
    }

#pragma warning disable CS0618 // The existing Toolkit renderer uses the legacy Markdown parser.
    private sealed class TableRenderer : MarkdownRenderer
    {
        public TableRenderer(MarkdownDocument document, ILinkRegister links, IImageResolver images, ICodeBlockResolver code)
            : base(document, links, images, code)
        {
        }

        protected override void RenderTable(TableBlock element, IRenderContext context)
        {
            base.RenderTable(element, context);
            ReplaceTable(context, element.ColumnDefinitions.Count, element.Rows.Count, TableBorderThickness, TableBorderBrush);
        }

        protected override void RenderYamlHeader(YamlHeaderBlock element, IRenderContext context)
        {
            base.RenderYamlHeader(element, context);
            ReplaceTable(context, element.Children.Count, 2, YamlBorderThickness, YamlBorderBrush);
        }

        private void ReplaceTable(IRenderContext context, int columns, int rows, double thickness, Microsoft.UI.Xaml.Media.Brush brush)
        {
            // Toolkit 7.x's managed MarkdownTable fails during MeasureOverride under Native AOT.
            // Keep its rendered cells, but let a native Grid perform layout instead.
            var blocks = ((UIElementCollectionRenderContext)context).BlockUIElementCollection;
            var original = (Panel)blocks[blocks.Count - 1];
            var table = new Grid
            {
                Margin = original.Margin,
                BorderBrush = brush,
                BorderThickness = new Thickness(thickness),
                FlowDirection = FlowDirection
            };
            for (int column = 0; column < columns; column++)
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int row = 0; row < rows; row++)
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            while (original.Children.Count > 0)
            {
                var child = original.Children[0];
                original.Children.RemoveAt(0);
                if (child is Rectangle) continue;

                var content = (FrameworkElement)child;
                int column = Grid.GetColumn(content);
                int row = Grid.GetRow(content);
                var cell = new Border
                {
                    Child = child,
                    BorderBrush = brush,
                    BorderThickness = new Thickness(0, 0,
                        column < columns - 1 ? thickness : 0,
                        row < rows - 1 ? thickness : 0)
                };
                Grid.SetColumn(cell, column);
                Grid.SetRow(cell, row);
                table.Children.Add(cell);
            }
            blocks[blocks.Count - 1] = table;
        }
    }
#pragma warning restore CS0618
}
