/*
 * 本文件基于 [Iridium] 的代码修改
 * 原始项目: [https://github.com/Xbodwf/Iridium]
 * 原始许可证: 无
 * 新增更多键控支持
 */
using System;
using System.Collections.Generic;
using UnityEngine;

#nullable enable

namespace ADOFAIMacro
{
    public static class UIUtils
    {
        private static GUIStyle? _cardStyle;
        private static GUIStyle? _headerStyle;
        private static GUIStyle? _buttonStyle;
        private static GUIStyle? _labelStyle;
        private static GUIStyle? _textFieldStyle;
        private static GUIStyle? _infoBoxStyle;
        private static GUIStyle? _warningBoxStyle;
        private static GUIStyle? _selectionGridStyle;
        // 键用值元组，避免每次查找都做字符串插值（此缓存每帧命中几十次）
        private static readonly Dictionary<(int w, int h, float rad, float cr, float cg, float cb, float ca, bool tl, bool tr, bool bl, bool br), Texture2D> _textureCache = [];

        public static GUIStyle CardStyle => _cardStyle ?? throw new InvalidOperationException("UI not initialized");
        public static GUIStyle HeaderStyle => _headerStyle ?? throw new InvalidOperationException("UI not initialized");
        public static GUIStyle ButtonStyle => _buttonStyle ?? throw new InvalidOperationException("UI not initialized");
        public static GUIStyle LabelStyle => _labelStyle ?? throw new InvalidOperationException("UI not initialized");
        public static GUIStyle TextFieldStyle => _textFieldStyle ?? throw new InvalidOperationException("UI not initialized");

        public static GUIStyle SelectionGridStyle => _selectionGridStyle ?? throw new InvalidOperationException("UI not initialized");

        public static void InitializeStyles()
        {
            if (_cardStyle != null) return;

            // Android 14 / Material 3 Dark Palette
            Color surfaceContainer = new(0.13f, 0.13f, 0.15f);
            Color primary = new(0.66f, 0.76f, 1.0f);
            Color onSurface = new(0.88f, 0.88f, 0.9f);
            Color surfaceContainerHigh = new(0.17f, 0.17f, 0.19f);
            Color errorContainer = new(0.35f, 0.1f, 0.1f);
            Color onErrorContainer = new(1.0f, 0.7f, 0.7f);
            Color infoContainer = new(0.1f, 0.2f, 0.35f);
            Color onInfoContainer = new(0.7f, 0.85f, 1.0f);
            Color onSurfaceVariant = new(0.75f, 0.75f, 0.78f);

            _cardStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 12, 12),
                margin = new RectOffset(0, 0, 6, 6),
                normal = { background = GetCachedRoundedTex(128, 128, 12, surfaceContainer) }
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Normal,
                normal = { textColor = primary },
                margin = new RectOffset(0, 0, 0, 8)
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = onSurface },
                alignment = TextAnchor.MiddleLeft
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fixedHeight = 28,
                padding = new RectOffset(12, 12, 0, 0),
                normal = { background = GetCachedRoundedTex(64, 64, 8, surfaceContainerHigh), textColor = primary },
                hover = { background = GetCachedRoundedTex(64, 64, 8, primary * 0.2f), textColor = Color.white },
                active = { background = GetCachedRoundedTex(64, 64, 8, primary), textColor = Color.black }
            };

            _textFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 12,
                fixedHeight = 24,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 0, 0),
                // 提高非 Hover 状态下的亮度，并增加微弱的边框感（通过颜色对比）
                normal = { background = GetCachedRoundedTex(64, 64, 4, new Color(0.25f, 0.25f, 0.28f)), textColor = onSurface },
                hover = { background = GetCachedRoundedTex(64, 64, 4, new Color(0.35f, 0.35f, 0.4f)), textColor = Color.white },
                focused = { background = GetCachedRoundedTex(64, 64, 4, new Color(0.4f, 0.4f, 0.45f)), textColor = Color.white }
            };

            _infoBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
                alignment = TextAnchor.MiddleLeft,
                fontSize = 12,
                normal = { background = GetCachedRoundedTex(64, 64, 8, infoContainer), textColor = onInfoContainer }
            };

            _warningBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
                alignment = TextAnchor.MiddleLeft,
                fontSize = 12,
                normal = { background = GetCachedRoundedTex(64, 64, 8, errorContainer), textColor = onErrorContainer }
            };

            _selectionGridStyle = new GUIStyle
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(4, 4, 4, 4),
                normal = { background = GetCachedRoundedTex(64, 64, 4, surfaceContainerHigh), textColor = onSurfaceVariant },
                hover = { background = GetCachedRoundedTex(64, 64, 4, primary * 0.2f), textColor = Color.white },
                active = { background = GetCachedRoundedTex(64, 64, 4, primary), textColor = Color.black },
                onNormal = { background = GetCachedRoundedTex(64, 64, 4, primary), textColor = Color.black },
                onHover = { background = GetCachedRoundedTex(64, 64, 4, primary), textColor = Color.black },
                onActive = { background = GetCachedRoundedTex(64, 64, 4, primary), textColor = Color.black }
            };

            // 预构建 SelectionGrid 按钮样式矩阵 [选中, 首个, 末个]
            // （原先每个按钮每帧 new 一个 GUIStyle，页签栏一次 OnGUI 要分配 9+ 个）
            _selGridStyles = new GUIStyle[2, 2, 2];
            for (int s = 0; s < 2; s++)
                for (int f = 0; f < 2; f++)
                    for (int l = 0; l < 2; l++)
                        _selGridStyles[s, f, l] = BuildSelGridStyle(s == 1, f == 1, l == 1);
        }

        // ─────────────────────────────────────────────
        //  Label 样式缓存：Settings 各卡原先每帧 new GUIStyle(Clone)
        // ─────────────────────────────────────────────
        private static readonly Dictionary<(float r, float g, float b, float a, int size, bool wrap, bool rich, int fs, int align), GUIStyle> _labelVariantCache = [];

        public static GUIStyle LabelStyleVariant(float r, float g, float b, float a,
            int fontSize = 13, bool wordWrap = false, bool richText = false,
            FontStyle fontStyle = FontStyle.Normal, TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var key = (r, g, b, a, fontSize, wordWrap, richText, (int)fontStyle, (int)alignment);
            if (_labelVariantCache.TryGetValue(key, out GUIStyle s) && s != null) return s;
            s = new GUIStyle(LabelStyle)
            {
                fontSize = fontSize,
                wordWrap = wordWrap,
                richText = richText,
                fontStyle = fontStyle,
                alignment = alignment,
                normal = { textColor = new Color(r, g, b, a) }
            };
            _labelVariantCache[key] = s;
            return s;
        }

        // ─────────────────────────────────────────────
        //  SelectionGrid 按钮样式（按 选中/首/末 组合缓存）
        // ─────────────────────────────────────────────
        private static GUIStyle[,,]? _selGridStyles;

        private static GUIStyle BuildSelGridStyle(bool isSelected, bool first, bool last)
        {
            Color primary = new(0.66f, 0.76f, 1.0f);
            Color surfaceContainerHigh = new(0.17f, 0.17f, 0.19f);
            Color onSurfaceVariant = new(0.75f, 0.75f, 0.78f);
            bool tl = first, tr = last, bl = first, br = last;
            const float radius = 8;

            GUIStyle style = new(_selectionGridStyle)
            {
                fixedHeight = 28,
                margin = new RectOffset(1, 1, 0, 0),
                padding = new RectOffset(4, 4, 4, 4)
            };
            if (isSelected)
            {
                style.normal.background = GetCachedRoundedTex(64, 64, radius, primary, tl, tr, bl, br);
                style.normal.textColor = Color.black;
                style.hover.background = GetCachedRoundedTex(64, 64, radius, primary * 1.1f, tl, tr, bl, br);
                style.hover.textColor = Color.black;
            }
            else
            {
                style.normal.background = GetCachedRoundedTex(64, 64, radius, surfaceContainerHigh, tl, tr, bl, br);
                style.normal.textColor = onSurfaceVariant;
                style.hover.background = GetCachedRoundedTex(64, 64, radius, primary * 0.3f, tl, tr, bl, br);
                style.hover.textColor = Color.white;
            }
            return style;
        }

        public static void DrawInfoBox(string text, bool isError = false)
        {
            GUILayout.Box(text, isError ? _warningBoxStyle : _infoBoxStyle, GUILayout.ExpandWidth(true));
        }

        /// <summary>卡片内分隔线（原先在 Settings 中重复 6 次）。</summary>
        public static void DrawSeparator(float alpha = 0.3f, float height = 1f)
        {
            Color oc = GUI.color;
            GUI.color = new Color(0.5f, 0.5f, 0.5f, alpha);
            GUILayout.Box("", GUILayout.Height(height), GUILayout.ExpandWidth(true));
            GUI.color = oc;
        }

        public static bool M3Switch(bool value, string label)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(32));
            if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, _labelStyle, GUILayout.ExpandWidth(true));

            Color trackColor = value ? new(0.66f, 0.76f, 1.0f) : new(0.28f, 0.28f, 0.31f);
            Color thumbColor = value ? new(0.0f, 0.2f, 0.4f) : new(0.55f, 0.55f, 0.58f);

            Rect rect = GUILayoutUtility.GetRect(40, 24, GUILayout.Width(40), GUILayout.Height(24));

            GUI.color = trackColor;
            GUI.DrawTexture(rect, GetCachedRoundedTex(64, 32, 16, Color.white));

            float thumbSize = 18;
            float thumbX = value ? rect.x + rect.width - thumbSize - 3 : rect.x + 3;
            Rect thumbRect = new(thumbX, rect.y + (rect.height - thumbSize) / 2, thumbSize, thumbSize);
            GUI.color = thumbColor;
            GUI.DrawTexture(thumbRect, GetCachedRoundedTex(32, 32, 16, Color.white));

            GUI.color = Color.white;
            if (GUI.Button(rect, "", GUIStyle.none)) value = !value;

            GUILayout.EndHorizontal();
            return value;
        }

        public static Texture2D GetCachedRoundedTex(int width, int height, float radius, Color col, bool tl = true, bool tr = true, bool bl = true, bool br = true)
        {
            var key = (width, height, radius, col.r, col.g, col.b, col.a, tl, tr, bl, br);
            if (_textureCache.TryGetValue(key, out Texture2D tex) && tex != null) return tex;

            tex = MakeRoundedTex(width, height, radius, col, tl, tr, bl, br);
            tex.hideFlags = HideFlags.HideAndDontSave;
            _textureCache[key] = tex;
            return tex;
        }

        private static Texture2D MakeRoundedTex(int width, int height, float radius, Color col, bool tl = true, bool tr = true, bool bl = true, bool br = true)
        {
            Texture2D tex = new(width, height);
            Color[] pix = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dx = -1, dy = -1;
                    bool isCornerRegion = false;

                    // Top-Left
                    if (tl && x < radius && y >= height - radius) { dx = radius - x; dy = radius - (height - 1 - y); isCornerRegion = true; }
                    // Top-Right
                    else if (tr && x >= width - radius && y >= height - radius) { dx = radius - (width - 1 - x); dy = radius - (height - 1 - y); isCornerRegion = true; }
                    // Bottom-Left
                    else if (bl && x < radius && y < radius) { dx = radius - x; dy = radius - y; isCornerRegion = true; }
                    // Bottom-Right
                    else if (br && x >= width - radius && y < radius) { dx = radius - (width - 1 - x); dy = radius - y; isCornerRegion = true; }

                    if (isCornerRegion)
                    {
                        float d = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (d > radius)
                        {
                            pix[y * width + x] = Color.clear;
                        }
                        else
                        {
                            float alpha = Math.Min(1, radius + 0.5f - d);
                            pix[y * width + x] = new Color(col.r, col.g, col.b, col.a * alpha);
                        }
                    }
                    else
                    {
                        pix[y * width + x] = col;
                    }
                }
            }

            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        public static float M3HorizontalSlider(float value, float leftValue, float rightValue, params GUILayoutOption[] options)
        {
            // 保存原始颜色
            Color originalColor = GUI.color;
            Color originalBackgroundColor = GUI.backgroundColor;
            Color originalContentColor = GUI.contentColor;

            // 定义颜色 - Material 3 配色
            Color trackColor = new(0.28f, 0.28f, 0.31f);           // 轨道背景色
            Color progressColor = new(0.66f, 0.76f, 1.0f);        // 进度条颜色
            Color thumbColor = new(0.9f, 0.9f, 0.95f);             // 滑块颜色
            Color hoverThumbColor = new(1.0f, 1.0f, 1.0f);         // 悬停时滑块颜色

            // 计算当前进度
            float normalizedValue = (value - leftValue) / (rightValue - leftValue);
            normalizedValue = Mathf.Clamp01(normalizedValue);

            // 获取滑块控制ID
            int controlID = GUIUtility.GetControlID(FocusType.Passive);
            Event currentEvent = Event.current;

            // 处理鼠标交互
            bool isDragging = GUIUtility.hotControl == controlID;

            // 先通过GUILayout获取一个水平区域
            GUILayout.BeginHorizontal();

            // 获取滑动条区域 - 固定高度为24像素
            Rect sliderRect = GUILayoutUtility.GetRect(0, 24, options);

            // 添加一些左右边距
            sliderRect.xMin += 4;
            sliderRect.xMax -= 4;

            // 检查鼠标是否悬停在滑块或轨道上
            float thumbSize = 18; // 稍微增大滑块，更容易点击
            float thumbY = sliderRect.y + (sliderRect.height - thumbSize) / 2;
            float thumbCenterX = sliderRect.x + (sliderRect.width * normalizedValue);
            float thumbX = thumbCenterX - thumbSize / 2;
            Rect thumbRect = new(thumbX, thumbY, thumbSize, thumbSize);

            bool isHovering = sliderRect.Contains(currentEvent.mousePosition) || thumbRect.Contains(currentEvent.mousePosition);

            // 处理鼠标事件
            switch (currentEvent.GetTypeForControl(controlID))
            {
                case EventType.MouseDown:
                    if (sliderRect.Contains(currentEvent.mousePosition) || thumbRect.Contains(currentEvent.mousePosition))
                    {
                        GUIUtility.hotControl = controlID;

                        // 直接跳转到点击位置
                        float clickValue = (currentEvent.mousePosition.x - sliderRect.x) / sliderRect.width;
                        value = leftValue + clickValue * (rightValue - leftValue);
                        value = Mathf.Clamp(value, leftValue, rightValue);

                        currentEvent.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlID)
                    {
                        float dragValue = (currentEvent.mousePosition.x - sliderRect.x) / sliderRect.width;
                        value = leftValue + dragValue * (rightValue - leftValue);
                        value = Mathf.Clamp(value, leftValue, rightValue);
                        currentEvent.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID)
                    {
                        GUIUtility.hotControl = 0;
                        currentEvent.Use();
                    }
                    break;

                case EventType.Repaint:
                    // 在Repaint阶段绘制所有UI元素

                    // 纹理宽度按 16px 档位量化：纹理本身会被拉伸绘制，
                    // 不需要精确到像素宽度，避免拖动窗口大小时缓存无限增长
                    const int radius = 12;
                    int trackTexW = Math.Max(32, ((int)sliderRect.width + 15) & ~15);

                    // 1. 先绘制轨道背景（最底层）
                    GUI.color = trackColor;
                    GUI.DrawTexture(sliderRect, GetCachedRoundedTex(trackTexW, 24, radius, Color.white));

                    // 2. 再绘制进度条（中间层）
                    float progressWidth = sliderRect.width * normalizedValue;
                    if (progressWidth > 2) // 至少显示一点进度
                    {
                        Rect progressRect = new(sliderRect.x, sliderRect.y, progressWidth, sliderRect.height);
                        int progTexW = Math.Max(16, ((int)progressWidth + 15) & ~15);
                        GUI.color = progressColor;
                        GUI.DrawTexture(progressRect, GetCachedRoundedTex(progTexW, 24, radius, Color.white));
                    }

                    // 3. 最后绘制滑块（最上层）- 移到进度条上面
                    GUI.color = isDragging || isHovering ? hoverThumbColor : thumbColor;
                    float currentThumbSize = isDragging ? thumbSize + 2 : thumbSize; // 拖动时稍微放大
                    float currentThumbX = sliderRect.x + (sliderRect.width * normalizedValue) - currentThumbSize / 2;
                    float currentThumbY = sliderRect.y + (sliderRect.height - currentThumbSize) / 2;
                    Rect currentThumbRect = new(currentThumbX, currentThumbY, currentThumbSize, currentThumbSize);

                    // 绘制滑块阴影（稍微偏移，制造立体感）
                    GUI.color = new Color(0, 0, 0, 0.2f);
                    Rect shadowRect = new(currentThumbRect.x + 1, currentThumbRect.y + 1, currentThumbRect.width, currentThumbRect.height);
                    GUI.DrawTexture(shadowRect, GetCachedRoundedTex((int)currentThumbSize, (int)currentThumbSize, (int)(currentThumbSize / 2), Color.white));

                    // 绘制滑块本体
                    GUI.color = isDragging || isHovering ? hoverThumbColor : thumbColor;
                    GUI.DrawTexture(currentThumbRect, GetCachedRoundedTex((int)currentThumbSize, (int)currentThumbSize, (int)(currentThumbSize / 2), Color.white));

                    break;
            }

            GUILayout.EndHorizontal();

            // 恢复颜色
            GUI.color = originalColor;
            GUI.backgroundColor = originalBackgroundColor;
            GUI.contentColor = originalContentColor;

            return value;
        }

        // 带输入框滑动条方法
        public static float M3HorizontalSliderWithLabelAndInput(string label, float value, float leftValue, float rightValue,
            ref string inputText, ref bool isFocused, string format = "F2", float labelWidth = 60, float sliderWidth = 120, float fieldWidth = 60)
        {
            GUILayout.BeginHorizontal();

            // 标签
            GUILayout.Label(label, _labelStyle, GUILayout.Width(labelWidth), GUILayout.Height(24));

            // 滑动条 - 注意：这里不能再用 Begin/EndHorizontal，因为 M3HorizontalSlider 内部已经有布局了
            float newValue = M3HorizontalSlider(value, leftValue, rightValue,
                GUILayout.MinWidth(sliderWidth),
                GUILayout.ExpandWidth(true));

            GUILayout.Space(8);

            // 输入框 - 使用垂直对齐辅助
            GUILayout.BeginVertical(GUILayout.Height(24));
            GUILayout.FlexibleSpace();
            GUI.SetNextControlName("SliderInputField_" + label);
            string newInput = GUILayout.TextField(inputText, TextFieldStyle,
                GUILayout.Width(fieldWidth),
                GUILayout.Height(36)); // 文本输入框的实际高度
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();

            // 焦点管理（保持不变）
            if (GUI.GetNameOfFocusedControl() == "SliderInputField_" + label)
            {
                if (!isFocused)
                {
                    inputText = newValue.ToString(format);
                    isFocused = true;
                }
                else
                {
                    inputText = newInput;
                }
            }
            else
            {
                if (isFocused)
                {
                    if (float.TryParse(inputText, out float parsedValue))
                    {
                        newValue = Mathf.Clamp(parsedValue, leftValue, rightValue);
                    }
                    isFocused = false;
                }
                inputText = newValue.ToString(format);
            }

            GUILayout.EndHorizontal();

            return newValue;
        }
        /// <summary>
        /// Material 3 风格的 SelectionGrid
        /// </summary>
        public static int M3SelectionGrid(int selected, string[] texts, int xCount, params GUILayoutOption[] options)
        {
            int newSelected = selected;
            var styles = _selGridStyles!;

            GUILayout.BeginHorizontal();

            for (int i = 0; i < texts.Length; i++)
            {
                GUIStyle buttonStyle = styles[selected == i ? 1 : 0, i == 0 ? 1 : 0, i == texts.Length - 1 ? 1 : 0];

                // 让按钮平分宽度
                if (GUILayout.Button(texts[i], buttonStyle, GUILayout.ExpandWidth(true)))
                {
                    newSelected = i;
                }
            }

            GUILayout.EndHorizontal();

            return newSelected;
        }
        private static int _textFieldCounter = 0;

        public static string M3TextField(string value, ref string input, ref bool focused, GUIStyle style, string controlName, params GUILayoutOption[] options)
        {
            // 如果没有提供控件名称，自动生成一个（但尽量由调用者传入稳定名称）
            if (string.IsNullOrEmpty(controlName))
            {
                controlName = "M3TextField_" + (_textFieldCounter++);
            }

            if (focused)
            {
                // 设置控件名称，以便后续检测焦点
                GUI.SetNextControlName(controlName);
                string newInput = GUILayout.TextField(input, style, options);

                // 如果输入发生变化，更新 input
                if (newInput != input)
                {
                    input = newInput;
                }

                // 检查是否失去焦点（焦点已移到其他控件）
                if (GUI.GetNameOfFocusedControl() != controlName)
                {
                    focused = false;
                    value = input; // 提交编辑后的值
                    return value;
                }

                // 仍处于焦点状态，返回当前编辑的内容
                return input;
            }
            else
            {
                // 非焦点状态：显示 value，并检查是否获得焦点
                GUI.SetNextControlName(controlName);
                string newValue = GUILayout.TextField(value, style, options);

                // 如果当前焦点落在此控件上，则进入编辑模式
                if (GUI.GetNameOfFocusedControl() == controlName)
                {
                    focused = true;
                    input = newValue; // 将当前内容同步到 input
                    return input;
                }

                // 没有获得焦点，直接返回当前显示的值（可能被外部修改，但应与传入的 value 一致）
                return newValue;
            }
        }
    }
}
