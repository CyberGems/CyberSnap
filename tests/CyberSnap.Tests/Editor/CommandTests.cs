using System.Drawing;
using CyberSnap.Models;
using CyberSnap.Models.Commands;
using Xunit;

namespace CyberSnap.Tests.Editor;

public sealed class CommandTests
{
    private static FakeContext NewCtx() => new();

    [Fact]
    public void AddAnnotationCommand_ApplyAdds_RevertRemoves()
    {
        var ctx = NewCtx();
        var arrow = new ArrowAnnotation(new Point(10, 10), new Point(50, 50), Color.Red);
        var cmd = new AddAnnotationCommand(arrow);

        cmd.Apply(ctx);
        Assert.Single(ctx.Annotations);
        Assert.Equal(arrow, ctx.Annotations[0]);

        cmd.Revert(ctx);
        Assert.Empty(ctx.Annotations);
    }

    [Fact]
    public void DeleteAnnotationCommand_ApplyRemoves_RevertRestores()
    {
        var ctx = NewCtx();
        var line = new LineAnnotation(new Point(0, 0), new Point(100, 100), Color.Blue);
        ctx.Annotations.Add(line);

        var cmd = new DeleteAnnotationCommand(0, line);
        cmd.Apply(ctx);
        Assert.Empty(ctx.Annotations);

        cmd.Revert(ctx);
        Assert.Single(ctx.Annotations);
        Assert.Equal(line, ctx.Annotations[0]);
    }

    [Fact]
    public void DeleteAnnotationCommand_RevertRestoresOriginalOrder()
    {
        var ctx = NewCtx();
        var first = new LineAnnotation(new Point(0, 0), new Point(10, 10), Color.Blue);
        var middle = new ArrowAnnotation(new Point(1, 1), new Point(20, 20), Color.Red);
        var last = new CircleShapeAnnotation(new Rectangle(5, 5, 12, 12), Color.Green);
        ctx.Annotations.AddRange(new Annotation[] { first, middle, last });

        var cmd = new DeleteAnnotationCommand(1, middle);
        cmd.Apply(ctx);
        Assert.Equal(new Annotation[] { first, last }, ctx.Annotations);

        cmd.Revert(ctx);
        Assert.Equal(new Annotation[] { first, middle, last }, ctx.Annotations);
    }

    [Fact]
    public void ReplaceAnnotationCommand_ApplyReplaces_RevertRestores()
    {
        var ctx = NewCtx();
        var original = new RectShapeAnnotation(new Rectangle(10, 20, 100, 80), Color.Green);
        var replacement = new RectShapeAnnotation(new Rectangle(20, 30, 120, 90), Color.Green);
        ctx.Annotations.Add(original);

        var cmd = new ReplaceAnnotationCommand(0, original, replacement);
        cmd.Apply(ctx);
        Assert.Equal(replacement, ctx.Annotations[0]);

        cmd.Revert(ctx);
        Assert.Equal(original, ctx.Annotations[0]);
    }

    [Fact]
    public void TransformAnnotationCommand_TranslatesAndReverts()
    {
        var ctx = NewCtx();
        var original = new RectShapeAnnotation(new Rectangle(10, 20, 100, 80), Color.Green);
        ctx.Annotations.Add(original);

        var cmd = new TransformAnnotationCommand(original, 0, 5, -3);
        cmd.Apply(ctx);

        var applied = Assert.IsType<RectShapeAnnotation>(ctx.Annotations[0]);
        Assert.Equal(new Rectangle(15, 17, 100, 80), applied.Rect);

        cmd.Revert(ctx);
        var reverted = Assert.IsType<RectShapeAnnotation>(ctx.Annotations[0]);
        Assert.Equal(original.Rect, reverted.Rect);
    }

    [Fact]
    public void TransformAnnotationCommand_ArrowTranslation()
    {
        var ctx = NewCtx();
        var original = new ArrowAnnotation(new Point(0, 0), new Point(10, 10), Color.Black);
        ctx.Annotations.Add(original);

        var cmd = new TransformAnnotationCommand(original, 0, 3, 4);
        cmd.Apply(ctx);

        var applied = Assert.IsType<ArrowAnnotation>(ctx.Annotations[0]);
        Assert.Equal(new Point(3, 4), applied.From);
        Assert.Equal(new Point(13, 14), applied.To);

        cmd.Revert(ctx);
        var reverted = Assert.IsType<ArrowAnnotation>(ctx.Annotations[0]);
        Assert.Equal(original.From, reverted.From);
        Assert.Equal(original.To, reverted.To);
    }

    [Fact]
    public void TransformAnnotationCommand_DrawStrokeTranslation()
    {
        var ctx = NewCtx();
        var original = new DrawStroke(new List<Point> { new(1, 2), new(3, 4) }, Color.Red);
        ctx.Annotations.Add(original);

        var cmd = new TransformAnnotationCommand(original, 0, 10, 20);
        cmd.Apply(ctx);

        var applied = Assert.IsType<DrawStroke>(ctx.Annotations[0]);
        Assert.Equal(new Point(11, 22), applied.Points[0]);
        Assert.Equal(new Point(13, 24), applied.Points[1]);

        cmd.Revert(ctx);
        var reverted = Assert.IsType<DrawStroke>(ctx.Annotations[0]);
        Assert.Equal(original.Points[0], reverted.Points[0]);
        Assert.Equal(original.Points[1], reverted.Points[1]);
    }

    [Fact]
    public void TransformAnnotationCommand_TextTranslation()
    {
        var ctx = NewCtx();
        var original = new TextAnnotation(new Point(5, 5), "hello", 12f, Color.White, false, false, false, false, false, "Arial");
        ctx.Annotations.Add(original);

        var cmd = new TransformAnnotationCommand(original, 0, 2, 3);
        cmd.Apply(ctx);

        var applied = Assert.IsType<TextAnnotation>(ctx.Annotations[0]);
        Assert.Equal(new Point(7, 8), applied.Pos);

        cmd.Revert(ctx);
        var reverted = Assert.IsType<TextAnnotation>(ctx.Annotations[0]);
        Assert.Equal(original.Pos, reverted.Pos);
    }

    [Fact]
    public void TransformAnnotationCommand_OutOfBounds_IsNoOp()
    {
        var ctx = NewCtx();
        var original = new LineAnnotation(new Point(0, 0), new Point(1, 1), Color.Black);

        var cmd = new TransformAnnotationCommand(original, 5, 1, 1);
        cmd.Apply(ctx);   // index 5 does not exist
        Assert.Empty(ctx.Annotations);

        cmd.Revert(ctx);  // index 5 does not exist
        Assert.Empty(ctx.Annotations);
    }

    [Fact]
    public void AddAnnotationCommand_DescriptionContainsTypeName()
    {
        var cmd = new AddAnnotationCommand(new CircleShapeAnnotation(new Rectangle(0, 0, 10, 10), Color.Red));
        Assert.Contains("CircleShapeAnnotation", cmd.Description);
    }

    [Fact]
    public void TransformAnnotationCommand_Description_IsMove()
    {
        var cmd = new TransformAnnotationCommand(
            new ArrowAnnotation(Point.Empty, Point.Empty, Color.Black), 0, 0, 0);
        Assert.Equal("Move annotation", cmd.Description);
    }

    private sealed class FakeContext : IEditorContext
    {
        public Bitmap BaseBitmap { get; set; } = new(1, 1);
        public List<Annotation> Annotations { get; } = new();
        public int InvalidateCount { get; private set; }
        public void Invalidate() => InvalidateCount++;
    }
}
