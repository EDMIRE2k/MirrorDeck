using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MirrorDeck.PreviewHost;

internal sealed class ReShadeTipPanel : Control
{
	private readonly Button _closeButton;

	public event EventHandler? CloseRequested;

	public ReShadeTipPanel()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		BackColor = Color.Transparent;
		Visible = false;
		_closeButton = new Button
		{
			Text = "Close",
			FlatStyle = FlatStyle.Flat,
			ForeColor = Color.White,
			BackColor = Color.FromArgb(62, 72, 90),
			TabStop = false,
			Anchor = (AnchorStyles.Top | AnchorStyles.Right)
		};
		_closeButton.FlatAppearance.BorderColor = Color.FromArgb(105, 118, 140);
		_closeButton.Click += (object? _, EventArgs _) =>
		{
			CloseRequested?.Invoke(this, EventArgs.Empty);
		};
		Controls.Add(_closeButton);
	}

	protected override void OnResize(EventArgs e)
	{
		base.OnResize(e);
		_closeButton.SetBounds(Math.Max(0, Width - 82), 10, 66, 28);
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
		using SolidBrush brush = new SolidBrush(Color.FromArgb(226, 17, 22, 31));
		using Pen pen = new Pen(Color.FromArgb(210, 111, 231, 255), 1f);
		using SolidBrush brush2 = new SolidBrush(Color.White);
		using SolidBrush brush3 = new SolidBrush(Color.FromArgb(232, 215, 224, 234));
		Rectangle clientRectangle = ClientRectangle;
		clientRectangle.Inflate(-1, -1);
		e.Graphics.FillRectangle(brush, clientRectangle);
		e.Graphics.DrawRectangle(pen, clientRectangle);
		using Font font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
		using Font font2 = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
		e.Graphics.DrawString("ReShade controls DLSS 5", font, brush2, 14f, 13f);
		e.Graphics.DrawString("Press HOME to open ReShade. In RenoDX-DLSSNR, tune NR Intensity, Preset, Style, and Strength.\nFor before/after checks, use NR Intensity. Avoid disabling DLSS Neural Rendering unless you want DLSS fully off.", font2, brush3, new RectangleF(14f, 44f, Math.Max(50, Width - 28), Height - 52));
	}
}
