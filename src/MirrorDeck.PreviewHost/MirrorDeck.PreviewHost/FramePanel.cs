using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MirrorDeck.PreviewHost;

internal sealed class FramePanel : Panel
{
	private Bitmap? _frame;

	private bool _gpuActive;

	public FramePanel()
	{
		SetStyle(ControlStyles.Selectable, value: true);
		TabStop = true;
		DoubleBuffered = true;
		ResizeRedraw = true;
	}

	protected override bool IsInputKey(Keys keyData)
	{
		return true;
	}

	public void SetFrame(Bitmap frame)
	{
        DoubleBuffered = true;
		Bitmap? frame2 = _frame;
		_frame = frame;
		_gpuActive = false;
		Invalidate();
		frame2?.Dispose();
	}

	public void SetGpuActive()
	{
		if (!_gpuActive)
		{
            DoubleBuffered = false;
			Bitmap? frame = _frame;
			_frame = null;
			_gpuActive = true;
			frame?.Dispose();
		}
	}

	public void ClearFrame()
	{
		Bitmap? frame = _frame;
		_frame = null;
		_gpuActive = false;
		frame?.Dispose();
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		if (_gpuActive)
		{
			return;
		}
		e.Graphics.Clear(Color.Black);
		if (_frame == null)
		{
			using (SolidBrush brush = new SolidBrush(Color.FromArgb(210, 240, 244, 248)))
			{
				using StringFormat format = new StringFormat
				{
					Alignment = StringAlignment.Center,
					LineAlignment = StringAlignment.Center
				};
				e.Graphics.DrawString("Waiting for video signal...", Font, brush, ClientRectangle, format);
				return;
			}
		}
		e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
		e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
		e.Graphics.DrawImage(_frame, ClientRectangle);
	}

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!_gpuActive) base.OnPaintBackground(e);
    }
}
