using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.Wave;
using System.Reflection;

namespace Bop;

public class PlayerForm : Form
{
    public static Icon? GetEmbeddedIcon(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"Bop.{resourceName}");
        return stream != null ? new Icon(stream) : null;
    }
    private readonly Action<float> _onVolumeChanged;
    private readonly Action<double> _onSeekRequested;
    private readonly Action _onPlayPauseToggled;
    private readonly Action _onStopRequested;
    private WaveOutEvent? _outputDevice;
    private MediaFoundationReader? _audioFile;
    private string _songTitle = "Title of the video";
    private string _artistName = "YouTube channel";
    private bool _isUserSeeking = false;
    private bool _isUserVoluming = false;
    private double _currentProgress = 0.0;
    private float _currentVolume = 0.5f;
    private Image? _coverImage = null;
    private static readonly HttpClient _httpClient = new();
    private bool _isDarkMode = false;
    private Color _bgColor;
    private Color _primaryTextColor;
    private Color _secondaryTextColor;
    private Color _iconColor;
    private Color _trackBgColor;
    private Color _coverBgColor;
    private Rectangle _playBtnBounds;
    private Rectangle _prevBtnBounds;
    private Rectangle _nextBtnBounds;
    private Rectangle _closeBtnBounds;
    private Rectangle _seekBarBounds;
    private Rectangle _volumeBarBounds;
    private readonly System.Windows.Forms.Timer _updateTimer;

    public PlayerForm(
        Action<float> onVolumeChanged,
        Action<double> onSeekRequested,
        Action onPlayPauseToggled,
        Action onStopRequested)
    {
        _onVolumeChanged = onVolumeChanged;
        _onSeekRequested = onSeekRequested;
        _onPlayPauseToggled = onPlayPauseToggled;
        _onStopRequested = onStopRequested;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(340, 420);
        TopMost = true;
        DoubleBuffered = true;

        var primaryScreen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(primaryScreen.Right - Width - 30, primaryScreen.Bottom - Height - 30);

        _closeBtnBounds = new Rectangle(Width - 25, 10, 12, 12);
        _seekBarBounds = new Rectangle(25, 250, 290, 10);

        _prevBtnBounds = new Rectangle(65, 295, 36, 36);
        _playBtnBounds = new Rectangle(145, 288, 50, 50);
        _nextBtnBounds = new Rectangle(239, 295, 36, 36);

        _volumeBarBounds = new Rectangle(65, 370, 210, 10);

        ApplyRoundedRegion(24);
        UpdateThemeColors();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        _updateTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _updateTimer.Tick += UpdateProgress;

        MouseDown += PlayerForm_MouseDown;
        MouseUp += PlayerForm_MouseUp;
        MouseMove += PlayerForm_MouseMove;

        var icon = GetEmbeddedIcon("gmalalatete.ico");
        if (icon != null)
        {
            this.Icon = icon;
        }
        else
        {
            this.Icon = SystemIcons.Application; // Icône de secours si problème
        }
    }

    private bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var registryValue = key?.GetValue("AppsUseLightTheme");
            if (registryValue is int value)
            {
                return value == 0;
            }
        }
        catch { }
        return false;
    }

    private void UpdateThemeColors()
    {
        _isDarkMode = IsWindowsInDarkMode();

        if (_isDarkMode)
        {
            _bgColor = Color.FromArgb(30, 30, 35);
            _primaryTextColor = Color.FromArgb(245, 245, 250);
            _secondaryTextColor = Color.FromArgb(160, 160, 175);
            _iconColor = Color.FromArgb(240, 240, 245);
            _trackBgColor = Color.FromArgb(60, 60, 70);
            _coverBgColor = Color.FromArgb(100, 85, 165);
        }
        else
        {
            _bgColor = Color.White;
            _primaryTextColor = Color.FromArgb(40, 40, 50);
            _secondaryTextColor = Color.FromArgb(120, 120, 130);
            _iconColor = Color.FromArgb(50, 52, 56);
            _trackBgColor = Color.FromArgb(200, 202, 206);
            _coverBgColor = Color.FromArgb(200, 182, 255);
        }

        BackColor = _bgColor;
        Invalidate();
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
        {
            UpdateThemeColors();
        }
    }

    private void ApplyRoundedRegion(int radius)
    {
        using GraphicsPath path = GetRoundedRectPath(new Rectangle(0, 0, Width, Height), radius);
        Region = new Region(path);
    }

    public void BindMedia(
        string title, 
        string channelName = "YouTube Channel", 
        WaveOutEvent? outputDevice = null, 
        MediaFoundationReader? audioFile = null, 
        float currentVolume = 0.5f,
        string? thumbnailUrl = null)
    {
        _songTitle = title;
        _artistName = channelName;
        _outputDevice = outputDevice;
        _audioFile = audioFile;
        _currentVolume = currentVolume;

        // Nettoyer l'ancienne image
        _coverImage?.Dispose();
        _coverImage = null;

        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            _ = LoadThumbnailAsync(thumbnailUrl);
        }

        _updateTimer.Start();
        if (!Visible) Show();
        BringToFront();
        Invalidate();
    }

    private async Task LoadThumbnailAsync(string url)
    {
        try
        {
            byte[] bytes = await _httpClient.GetByteArrayAsync(url);
            using MemoryStream ms = new MemoryStream(bytes);
            Image downloadedImg = Image.FromStream(ms);
            
            _coverImage = (Image)downloadedImg.Clone();
            
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(Invalidate));
            }
        }
        catch
        {
            _coverImage = null;
        }
    }

    public void SetLoadingState(string statusText)
    {
        _songTitle = statusText;
        _artistName = "Loading...";
        _currentProgress = 0;

        _coverImage?.Dispose();
        _coverImage = null;

        if (!Visible) Show();
        BringToFront();
        Invalidate();
    }

    private bool _isDragging = false;
    private Point _dragCursorPoint;
    private Point _dragFormPoint;

    private void PlayerForm_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_closeBtnBounds.Contains(e.Location))
            {
                Hide();
                return;
            }

            if (_playBtnBounds.Contains(e.Location))
            {
                _onPlayPauseToggled();
                Invalidate();
                return;
            }

            if (_prevBtnBounds.Contains(e.Location))
            {
                SeekRelative(-5);
                return;
            }

            if (_nextBtnBounds.Contains(e.Location))
            {
                SeekRelative(5);
                return;
            }

            if (_seekBarBounds.Contains(e.Location))
            {
                _isUserSeeking = true;
                HandleSeek(e.X);
                return;
            }

            if (_volumeBarBounds.Contains(e.Location))
            {
                _isUserVoluming = true;
                HandleVolume(e.X);
                return;
            }

            _isDragging = true;
            _dragCursorPoint = Cursor.Position;
            _dragFormPoint = Location;
        }
    }

    private void PlayerForm_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            Point dif = Point.Subtract(Cursor.Position, new Size(_dragCursorPoint));
            Location = Point.Add(_dragFormPoint, new Size(dif));
        }
        else if (_isUserSeeking)
        {
            HandleSeek(e.X);
        }
        else if (_isUserVoluming)
        {
            HandleVolume(e.X);
        }
    }

    private void PlayerForm_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                _onSeekRequested(_currentProgress);
            }
            if (_isUserVoluming)
            {
                _isUserVoluming = false;
            }
        }
    }

    private void HandleSeek(int mouseX)
    {
        int relativeX = Math.Clamp(mouseX - _seekBarBounds.X, 0, _seekBarBounds.Width);
        _currentProgress = (double)relativeX / _seekBarBounds.Width;
        Invalidate();
    }

    private void HandleVolume(int mouseX)
    {
        int relativeX = Math.Clamp(mouseX - _volumeBarBounds.X, 0, _volumeBarBounds.Width);
        _currentVolume = (float)relativeX / _volumeBarBounds.Width;
        _onVolumeChanged(_currentVolume);
        Invalidate();
    }

    private void SeekRelative(double seconds)
    {
        if (_audioFile == null) return;
        var newTime = _audioFile.CurrentTime.Add(TimeSpan.FromSeconds(seconds));
        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
        if (newTime > _audioFile.TotalTime) newTime = _audioFile.TotalTime;
        _audioFile.CurrentTime = newTime;
    }

    private void UpdateProgress(object? sender, EventArgs e)
    {
        if (_audioFile != null && !_isUserSeeking && _audioFile.TotalTime.TotalSeconds > 0)
        {
            _currentProgress = _audioFile.CurrentTime.TotalSeconds / _audioFile.TotalTime.TotalSeconds;
        }
        Invalidate();
    }

    // --- RENDU GRAPHIQUE ---
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Fond principal
        g.Clear(_bgColor);

        // Miniature
        Rectangle coverRect = new Rectangle(20, 30, Width - 40, 130);
        using (GraphicsPath coverPath = GetRoundedRectPath(coverRect, 18))
        {
            if (_coverImage != null)
            {
                GraphicsState state = g.Save();
                g.SetClip(coverPath);

                // Rendu intelligent (Center-Crop sans déformation de la miniature 16:9)
                float imgRatio = (float)_coverImage.Width / _coverImage.Height;
                float rectRatio = (float)coverRect.Width / coverRect.Height;
                
                Rectangle drawRect = coverRect;
                if (imgRatio > rectRatio)
                {
                    int newWidth = (int)(coverRect.Height * imgRatio);
                    drawRect = new Rectangle(coverRect.X - (newWidth - coverRect.Width) / 2, coverRect.Y, newWidth, coverRect.Height);
                }
                else
                {
                    int newHeight = (int)(coverRect.Width / imgRatio);
                    drawRect = new Rectangle(coverRect.X, coverRect.Y - (newHeight - coverRect.Height) / 2, coverRect.Width, newHeight);
                }

                g.DrawImage(_coverImage, drawRect);
                g.Restore(state);
            }
            else
            {
                using SolidBrush coverBrush = new SolidBrush(_coverBgColor);
                g.FillPath(coverBrush, coverPath);
                DrawMusicNote(g, new Point(coverRect.X + coverRect.Width / 2, coverRect.Y + coverRect.Height / 2));
            }
        }

        // Croix de fermeture
        using (Pen closePen = new Pen(_primaryTextColor, 1.5f))
        {
            g.DrawLine(closePen, _closeBtnBounds.X, _closeBtnBounds.Y, _closeBtnBounds.Right, _closeBtnBounds.Bottom);
            g.DrawLine(closePen, _closeBtnBounds.Right, _closeBtnBounds.Y, _closeBtnBounds.X, _closeBtnBounds.Bottom);
        }

        // 4. Titre et Nom de la Chaîne YouTube
        using (Font titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold))
        using (Font artistFont = new Font("Segoe UI", 9f, FontStyle.Regular))
        using (SolidBrush titleBrush = new SolidBrush(_primaryTextColor))
        using (SolidBrush artistBrush = new SolidBrush(_secondaryTextColor))
        {
            StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(_songTitle, titleFont, titleBrush, new RectangleF(15, 172, Width - 30, 22), sf);
            g.DrawString(_artistName, artistFont, artistBrush, new RectangleF(15, 194, Width - 30, 18), sf);
        }

        // Barre de Progression + Temps
        using (Pen bgTrackPen = new Pen(_trackBgColor, 3))
        using (Pen fillTrackPen = new Pen(_iconColor, 3))
        {
            int yTrack = _seekBarBounds.Y + 3;
            g.DrawLine(bgTrackPen, _seekBarBounds.X, yTrack, _seekBarBounds.Right, yTrack);

            int progressWidth = (int)(_seekBarBounds.Width * Math.Clamp(_currentProgress, 0.0, 1.0));
            if (progressWidth > 0)
            {
                g.DrawLine(fillTrackPen, _seekBarBounds.X, yTrack, _seekBarBounds.X + progressWidth, yTrack);
            }

            using SolidBrush thumbBrush = new SolidBrush(_iconColor);
            g.FillEllipse(thumbBrush, _seekBarBounds.X + progressWidth - 5, yTrack - 5, 10, 10);
        }

        TimeSpan currentTS = _audioFile != null ? _audioFile.CurrentTime : TimeSpan.Zero;
        TimeSpan totalTS = _audioFile != null ? _audioFile.TotalTime : TimeSpan.Zero;

        using (Font timeFont = new Font("Segoe UI", 8.5f, FontStyle.Regular))
        using (SolidBrush timeBrush = new SolidBrush(_secondaryTextColor))
        {
            g.DrawString($"{currentTS:mm\\:ss}", timeFont, timeBrush, _seekBarBounds.X, _seekBarBounds.Y + 12);
            StringFormat sfRight = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString($"{totalTS:mm\\:ss}", timeFont, timeBrush, _seekBarBounds.Right, _seekBarBounds.Y + 12, sfRight);
        }

        // Boutons (-5s, Play/Pause, +5s)
        DrawCircularArrow(g, _prevBtnBounds, _iconColor, isForward: false);

        bool isPlaying = _outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing;
        if (isPlaying)
            DrawPauseIcon(g, _playBtnBounds, _iconColor);
        else
            DrawPlayIcon(g, _playBtnBounds, _iconColor);

        DrawCircularArrow(g, _nextBtnBounds, _iconColor, isForward: true);

        // Barre de Volume + Icônes
        DrawSpeakerIcon(g, new Point(_volumeBarBounds.X - 22, _volumeBarBounds.Y - 2), _secondaryTextColor, isHigh: false);

        using (Pen bgVolPen = new Pen(_trackBgColor, 3))
        using (Pen fillVolPen = new Pen(_iconColor, 3))
        {
            int yVol = _volumeBarBounds.Y + 3;
            g.DrawLine(bgVolPen, _volumeBarBounds.X, yVol, _volumeBarBounds.Right, yVol);

            int volWidth = (int)(_volumeBarBounds.Width * Math.Clamp(_currentVolume, 0.0f, 1.0f));
            if (volWidth > 0)
            {
                g.DrawLine(fillVolPen, _volumeBarBounds.X, yVol, _volumeBarBounds.X + volWidth, yVol);
            }

            using SolidBrush volThumb = new SolidBrush(_iconColor);
            g.FillEllipse(volThumb, _volumeBarBounds.X + volWidth - 9, yVol - 9, 18, 18);
        }

        DrawSpeakerIcon(g, new Point(_volumeBarBounds.Right + 8, _volumeBarBounds.Y - 2), _secondaryTextColor, isHigh: true);
    }

    // --- ICÔNES ---
    private void DrawMusicNote(Graphics g, Point center)
    {
        using SolidBrush b = new SolidBrush(Color.White);
        using Pen p = new Pen(Color.White, 3);
        g.FillEllipse(b, center.X - 18, center.Y + 6, 12, 8);
        g.DrawLine(p, center.X - 8, center.Y + 10, center.X - 8, center.Y - 14);
        g.DrawLine(p, center.X - 8, center.Y - 14, center.X + 8, center.Y - 20);
    }

    private void DrawPlayIcon(Graphics g, Rectangle r, Color color)
    {
        using SolidBrush b = new SolidBrush(color);
        PointF[] p = {
            new PointF(r.X + 16, r.Y + 10),
            new PointF(r.X + 16, r.Y + r.Height - 10),
            new PointF(r.X + r.Width - 10, r.Y + r.Height / 2)
        };
        g.FillPolygon(b, p);
    }

    private void DrawPauseIcon(Graphics g, Rectangle r, Color color)
    {
        using SolidBrush b = new SolidBrush(color);
        g.FillRectangle(b, r.X + 13, r.Y + 10, 8, r.Height - 20);
        g.FillRectangle(b, r.X + 28, r.Y + 10, 8, r.Height - 20);
    }

    private void DrawCircularArrow(Graphics g, Rectangle r, Color color, bool isForward)
    {
        int margin = 7;
        Rectangle arcRect = new Rectangle(r.X + margin, r.Y + margin, r.Width - margin * 2, r.Height - margin * 2);

        if (arcRect.Width <= 0 || arcRect.Height <= 0) return;

        using Pen p = new Pen(color, 2.0f);
        using CustomLineCap arrowCap = new AdjustableArrowCap(3.5f, 4.0f, true);
        p.CustomEndCap = arrowCap;

        if (isForward)
        {
            g.DrawArc(p, arcRect, 30, 290);
        }
        else
        {
            g.DrawArc(p, arcRect, 150, -290);
        }
    }

    private void DrawSpeakerIcon(Graphics g, Point pt, Color color, bool isHigh)
    {
        using SolidBrush b = new SolidBrush(color);
        using Pen p = new Pen(color, 2);
        g.FillRectangle(b, pt.X, pt.Y + 4, 3, 6);
        PointF[] pPolygon = { new PointF(pt.X + 3, pt.Y + 4), new PointF(pt.X + 7, pt.Y + 1), new PointF(pt.X + 7, pt.Y + 13), new PointF(pt.X + 3, pt.Y + 10) };
        g.FillPolygon(b, pPolygon);

        if (isHigh)
        {
            g.DrawArc(p, pt.X + 7, pt.Y + 2, 6, 10, -60, 120);
            g.DrawArc(p, pt.X + 9, pt.Y, 9, 14, -60, 120);
        }
        else
        {
            g.DrawArc(p, pt.X + 7, pt.Y + 3, 5, 8, -60, 120);
        }
    }

    private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;

        if (rect.Width < diameter || rect.Height < diameter)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;

        _coverImage?.Dispose();
        _coverImage = null;

        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}