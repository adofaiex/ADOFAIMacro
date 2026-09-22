using System;
using UnityEngine;

namespace ADOFAIMacro.UI
{
    /// <summary>
    /// 极简 2D 光栅器：以像素缓冲替代 Iridium 原本的 System.Drawing(GDI+) 贴图生成。
    /// 仅实现布局用到的图元（矩形/圆角矩形/圆/线段/折线/圆弧），全部带边缘抗锯齿。
    /// 输出为直通（straight-alpha）颜色，与 GDI+ 32bppArgb 行为一致。
    /// </summary>
    internal sealed class Canvas
    {
        private readonly int _w;
        private readonly int _h;
        private readonly float[] _r;
        private readonly float[] _g;
        private readonly float[] _b;
        private readonly float[] _a;

        public Canvas(int width, int height)
        {
            _w = Mathf.Max(1, width);
            _h = Mathf.Max(1, height);
            int n = _w * _h;
            _r = new float[n];
            _g = new float[n];
            _b = new float[n];
            _a = new float[n];
        }

        public Texture2D ToTexture()
        {
            var tex = new Texture2D(_w, _h, TextureFormat.ARGB32, false);
            var px = new Color[_w * _h];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color(_r[i], _g[i], _b[i], _a[i]);
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        /// <summary>以覆盖率覆盖混合一个像素（straight-alpha）。</summary>
        private void Blend(int x, int y, Color c, float cov)
        {
            if (cov <= 0f || x < 0 || y < 0 || x >= _w || y >= _h) return;
            if (cov > 1f) cov = 1f;
            float sa = c.a * cov;
            if (sa <= 0f) return;
            int i = y * _w + x;
            float da = _a[i];
            float oa = sa + da * (1f - sa);
            if (oa <= 0f) { _a[i] = 0f; return; }
            float inv = da * (1f - sa);
            _r[i] = (c.r * sa + _r[i] * inv) / oa;
            _g[i] = (c.g * sa + _g[i] * inv) / oa;
            _b[i] = (c.b * sa + _b[i] * inv) / oa;
            _a[i] = oa;
        }

        private readonly struct Bounds
        {
            public readonly int X0, X1, Y0, Y1;
            public Bounds(int x0, int y0, int x1, int y1, int w, int h)
            {
                X0 = Mathf.Max(0, x0); Y0 = Mathf.Max(0, y0);
                X1 = Mathf.Min(w - 1, x1); Y1 = Mathf.Min(h - 1, y1);
            }
        }

        public void FillRect(float x, float y, float w, float h, Color c)
        {
            if (w <= 0f || h <= 0f) return;
            float x0 = x, x1 = x + w, y0 = y, y1 = y + h;
            var b = new Bounds(Mathf.FloorToInt(x0), Mathf.FloorToInt(y0),
                Mathf.CeilToInt(x1) - 1, Mathf.CeilToInt(y1) - 1, _w, _h);
            for (int py = b.Y0; py <= b.Y1; py++)
            {
                float cy = Mathf.Min(y1, py + 1f) - Mathf.Max(y0, py);
                if (cy <= 0f) continue;
                for (int px = b.X0; px <= b.X1; px++)
                {
                    float cx = Mathf.Min(x1, px + 1f) - Mathf.Max(x0, px);
                    if (cx <= 0f) continue;
                    Blend(px, py, c, cx * cy);
                }
            }
        }

        public void FillRoundedRect(float x, float y, float w, float h, float radius, Color c)
        {
            if (w <= 0f || h <= 0f) return;
            radius = Mathf.Clamp(radius, 0f, Mathf.Min(w, h) * 0.5f);
            float cx = x + w * 0.5f, cy = y + h * 0.5f;
            float hx = w * 0.5f - radius, hy = h * 0.5f - radius;
            var b = new Bounds(Mathf.FloorToInt(x) - 1, Mathf.FloorToInt(y) - 1,
                Mathf.CeilToInt(x + w) + 1, Mathf.CeilToInt(y + h) + 1, _w, _h);
            for (int py = b.Y0; py <= b.Y1; py++)
            {
                float dy = Mathf.Max(Mathf.Abs(py + 0.5f - cy) - hy, 0f);
                for (int px = b.X0; px <= b.X1; px++)
                {
                    float dx = Mathf.Max(Mathf.Abs(px + 0.5f - cx) - hx, 0f);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    Blend(px, py, c, Mathf.Clamp(0.5f - dist, 0f, 1f));
                }
            }
        }

        public void FillCircle(float cx, float cy, float radius, Color c)
        {
            if (radius <= 0f) return;
            var b = new Bounds(Mathf.FloorToInt(cx - radius) - 1, Mathf.FloorToInt(cy - radius) - 1,
                Mathf.CeilToInt(cx + radius) + 1, Mathf.CeilToInt(cy + radius) + 1, _w, _h);
            for (int py = b.Y0; py <= b.Y1; py++)
            {
                float dy = py + 0.5f - cy;
                for (int px = b.X0; px <= b.X1; px++)
                {
                    float dx = px + 0.5f - cx;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    Blend(px, py, c, Mathf.Clamp(0.5f - dist, 0f, 1f));
                }
            }
        }

        public void StrokeLine(float x0, float y0, float x1, float y1, float width, Color c)
        {
            float half = Mathf.Max(width, 0.01f) * 0.5f;
            float minX = Mathf.Min(x0, x1) - half - 1f, maxX = Mathf.Max(x0, x1) + half + 1f;
            float minY = Mathf.Min(y0, y1) - half - 1f, maxY = Mathf.Max(y0, y1) + half + 1f;
            var b = new Bounds(Mathf.FloorToInt(minX), Mathf.FloorToInt(minY),
                Mathf.CeilToInt(maxX), Mathf.CeilToInt(maxY), _w, _h);
            float vx = x1 - x0, vy = y1 - y0;
            float len2 = vx * vx + vy * vy;
            for (int py = b.Y0; py <= b.Y1; py++)
            {
                float qy = py + 0.5f;
                for (int px = b.X0; px <= b.X1; px++)
                {
                    float qx = px + 0.5f;
                    float t = len2 > 1e-6f ? Mathf.Clamp01(((qx - x0) * vx + (qy - y0) * vy) / len2) : 0f;
                    float dx = qx - (x0 + t * vx), dy = qy - (y0 + t * vy);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    Blend(px, py, c, Mathf.Clamp(half + 0.5f - dist, 0f, 1f));
                }
            }
        }

        /// <summary>折线描边（各段胶囊叠加，自动获得圆角连接与圆端帽）。</summary>
        public void StrokePolyline(Vector2[] points, float width, Color c, bool closed = false)
        {
            if (points == null || points.Length < 2) return;
            for (int i = 0; i < points.Length - 1; i++)
                StrokeLine(points[i].x, points[i].y, points[i + 1].x, points[i + 1].y, width, c);
            if (closed)
                StrokeLine(points[points.Length - 1].x, points[points.Length - 1].y, points[0].x, points[0].y, width, c);
        }

        /// <summary>圆弧描边；角度为 GDI+ 约定（y 轴向下，角度顺时针）。</summary>
        public void StrokeArc(float cx, float cy, float rx, float ry, float startDeg, float sweepDeg, float width, Color c)
        {
            bool full = Mathf.Abs(sweepDeg) >= 359.9f;
            int steps = Mathf.Max(8, Mathf.CeilToInt(Mathf.Abs(sweepDeg) / 6f));
            if (full) steps = Mathf.Max(steps, 48);
            var pts = new Vector2[full ? steps : steps + 1];
            for (int i = 0; i < pts.Length; i++)
            {
                float ang = (startDeg + sweepDeg * i / steps) * Mathf.Deg2Rad;
                pts[i] = new Vector2(cx + rx * Mathf.Cos(ang), cy + ry * Mathf.Sin(ang));
            }
            StrokePolyline(pts, width, c, full);
        }
    }
}
