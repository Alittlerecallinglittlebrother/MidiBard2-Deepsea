// Copyright (C) 2022 akira0245
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see https://github.com/akira0245/MidiBard/blob/master/LICENSE.
//
// This code is written by akira0245 and was originally used in the MidiBard project. Any usage of this code must prominently credit the author, akira0245, and indicate that it was originally used in the MidiBard project.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility;

using static Dalamud.api;

namespace MidiBard;

public static class ImGuiUtil
{
    public static bool EnumCombo<TEnum>(
    string label,
    ref TEnum @enum,
    ImGuiComboFlags flags = ImGuiComboFlags.None,
    bool showValue = false,
    string[]? toolTips = null,
    string[]? labelsOverride = null
    // Func<TEnum, object>? orderBy = null,
) where TEnum : struct, Enum
    {
        var ret = false;
        var enumValues = Enum.GetValues<TEnum>();
        var enumIndex = Array.IndexOf(enumValues, @enum);

        // preview text
        string selectedValue = labelsOverride != null && enumIndex >= 0 && enumIndex < labelsOverride.Length
            ? labelsOverride[enumIndex]
            : (showValue
                ? $"{@enum} ({Convert.ChangeType(@enum, @enum.GetTypeCode())})"
                : @enum.ToString());

        if (ImGui.BeginCombo(label, selectedValue, flags))
        {
            var values = enumValues;

            // if (orderBy != null)
            //     values = values.OrderBy(orderBy).ToArray();

            for (var i = 0; i < values.Length; i++)
            {
                try
                {
                    ImGui.PushID(i);

                    // Label
                    string itemLabel = labelsOverride != null && i < labelsOverride.Length
                        ? labelsOverride[i]
                        : (showValue
                            ? $"{values[i]} ({Convert.ChangeType(values[i], values[i].GetTypeCode())})"
                            : values[i].ToString());

                    if (ImGui.Selectable(itemLabel, values[i].Equals(@enum)))
                    {
                        ret = true;
                        @enum = values[i];
                    }

                    // Tooltip
                    if (toolTips != null && i < toolTips.Length && toolTips[i] != null && ImGui.IsItemHovered())
                    {
                        ToolTip(toolTips[i]);
                    }

                    ImGui.PopID();
                }
                catch (Exception e)
                {
                    PluginLog.Error(e.ToString());
                }
            }

            ImGui.EndCombo();
        }

        return ret;
    }

    public static Stack<Vector2> IconButtonSize = new Stack<Vector2>();

    public static void PushIconButtonSize(Vector2 size) => IconButtonSize.Push(size);
    public static void PopIconButtonSize() => IconButtonSize.TryPop(out _);

    public static Vector2 GetIconButtonSize(FontAwesomeIcon icon)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var size = ImGui.CalcTextSize(icon.ToIconString());
        ImGui.PopFont();
        return size;
    }

    public static bool IconButton(FontAwesomeIcon icon, string? id = null, string tooltip = null, Vector4? color = null)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        try
        {
            if (color != null) ImGui.PushStyleColor(ImGuiCol.Text, (Vector4)color);
            if (IconButtonSize.TryPeek(out var result))
            {
                return ImGui.Button($"{icon.ToIconString()}##{id}{tooltip}", result);
            }
            else
            {
                return ImGui.Button($"{icon.ToIconString()}##{id}{tooltip}");
            }
        }
        finally
        {
            ImGui.PopFont();
            if (color != null) ImGui.PopStyleColor();
            if (tooltip != null) ToolTip(tooltip);
        }
    }

    public static void HelpMarker(string description)
    {
        ImGui.SameLine();
        ImGuiUtil.DrawFontawesomeIconOutlined(FontAwesomeIcon.InfoCircle, Style.Colors.Black, Style.Components.TooltipBorderColor);
        ImGuiUtil.ToolTip(description);
    }

    public static void HelpMarker(string desc, bool sameline = true)
    {
        if (sameline) ImGui.SameLine();
        //ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextDisabled("(?)");
        //ImGui.PopFont();
        if (ImGui.IsItemHovered())
        {
            ImGui.PushFont(UiBuilder.DefaultFont);
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
            ImGui.TextUnformatted(desc);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
            ImGui.PopFont();
        }
    }

    public static bool ToggleButton(string id, ref bool v)
    {
        var colors = ImGui.GetStyle().Colors;
        var p = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var height = ImGui.GetFrameHeight();
        var width = height * 1.55f;
        var radius = height * 0.50f;

        var changed = false;
        ImGui.InvisibleButton(id, new Vector2(width, height));
        if (ImGui.IsItemClicked())
        {
            v = !v;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(p, new Vector2(p.X + width, p.Y + height), ImGui.GetColorU32(!v ? colors[(int)ImGuiCol.ButtonActive] : new Vector4(0.78f, 0.78f, 0.78f, 1.0f)), height * 0.5f);
        }
        else
        {
            drawList.AddRectFilled(p, new Vector2(p.X + width, p.Y + height), ImGui.GetColorU32(!v ? colors[(int)ImGuiCol.Button] * 0.6f : new Vector4(0.35f, 0.35f, 0.35f, 1.0f)), height * 0.50f);
        }

        drawList.AddCircleFilled(new Vector2(p.X + radius + ((v ? 1 : 0) * (width - (radius * 2.0f))), p.Y + radius), radius - 1.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)));

        return changed;
    }

    public static bool ToggleShowHideButton(string id, string tooltip, ref bool v)
    {
        var showHideIcon = v ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
        ImGui.PushStyleColor(ImGuiCol.Button, v ? Style.Components.ButtonSuccessNormal : Style.Components.ButtonDangerNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, v ? Style.Components.ButtonSuccessHovered : Style.Components.ButtonDangerHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, v ? Style.Components.ButtonSuccessActive : Style.Components.ButtonDangerActive);

        var changed = false;
        if (ImGuiUtil.IconButton(showHideIcon, id, tooltip))
        {
            v = !v;
            changed = true;
        }
        ImGui.PopStyleColor(3);

        return changed;
    }

    public static void IconButtonWithText(FontAwesomeIcon icon, string text, Vector2 size)
    {
        ImGuiComponents.IconButtonWithText(icon, text, size);
    }

    public static void Spacing(int amount = 1)
    {
        for (int i = 0; i < amount; i++)
        {
            ImGui.Spacing();
        }
    }

    public static void ToolTip(string desc, int wrap = 400, bool showBorder = true)
    {
        if (ImGui.IsItemHovered())
        {
            if (showBorder)
            {
                ImGui.PushStyleColor(ImGuiCol.Border, Style.Components.TooltipBorderColor);
                ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);
            }
            ImGui.PushFont(UiBuilder.DefaultFont);
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGuiHelpers.GlobalScale * wrap);
            ImGui.TextUnformatted(desc);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
            ImGui.PopFont();
            if (showBorder)
            {
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
            }
        }
    }

    public static unsafe void DrawColoredBanner(uint color, string content)
    {
        DrawColoredBanner(ImGui.ColorConvertU32ToFloat4(color), content);
    }

    public static unsafe void DrawColoredBanner(Vector4 color, string content)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        ImGui.Button(content, new Vector2(-1, ImGui.GetFrameHeight()));
        ImGui.PopStyleColor(3);
    }

    /// <summary>ColorPicker with palette with color picker options.</summary>
    /// <param name="id">Id for the color picker.</param>
    /// <param name="description">The description of the color picker.</param>
    /// <param name="originalColor">The current color.</param>
    /// <param name="flags">Flags to customize color picker.</param>
    /// <returns>Selected color.</returns>

    public static void ColorPickerWithPalette(int id, string description, ref Vector4 originalColor, ImGuiColorEditFlags flags)
    {
        Vector4 col = originalColor;
        List<Vector4> vector4List = ImGuiHelpers.DefaultColorPalette(36);
        if (ImGui.ColorButton(string.Format("{0}###ColorPickerButton{1}", (object)description, (object)id), originalColor, flags))
            ImGui.OpenPopup(string.Format("###ColorPickerPopup{0}", (object)id));
        if (ImGui.BeginPopup(string.Format("###ColorPickerPopup{0}", (object)id)))
        {
            if (ImGui.ColorPicker4(string.Format("###ColorPicker{0}", (object)id), ref col, flags))
            {
                originalColor = col;
            }
            for (int index1 = 0; index1 < 4; ++index1)
            {
                ImGui.Spacing();
                for (int index2 = index1 * 9; index2 < index1 * 9 + 9; ++index2)
                {
                    if (ImGui.ColorButton(string.Format("###ColorPickerSwatch{0}{1}{2}", (object)id, (object)index1, (object)index2), vector4List[index2]))
                    {
                        originalColor = vector4List[index2];
                        ImGui.CloseCurrentPopup();
                        ImGui.EndPopup();
                        return;
                    }
                    ImGui.SameLine();
                }
            }
            ImGui.EndPopup();
        }
    }

    public static void ColorPicker(int id, string description, ref Vector4 originalColor, ImGuiColorEditFlags flags)
    {
        Vector4 col = originalColor;
        if (ImGui.ColorButton($"{description}###ColorPickerButton{id}", originalColor, flags))
            ImGui.OpenPopup($"###ColorPickerPopup{id}");
        if (ImGui.BeginPopup($"###ColorPickerPopup{id}"))
        {
            if (ImGui.ColorPicker4($"###ColorPicker{id}", ref col, flags))
            {
                originalColor = col;
            }
            ImGui.EndPopup();
        }
    }

    public static void ColorPickerButton(int id, string description, ref Vector4 originalColor, ImGuiColorEditFlags flags)
    {
        Vector4 col = originalColor;
        if (ImGui.Button($"{description}###ColorPickerButton{id}"))
            ImGui.OpenPopup($"###ColorPickerPopup{id}");
        if (ImGui.BeginPopup($"###ColorPickerPopup{id}"))
        {
            if (ImGui.ColorPicker4($"###ColorPicker{id}", ref col, flags))
            {
                originalColor = col;
            }
            ImGui.EndPopup();
        }
    }

    public static void AddNotification(NotificationType type, string content)
    {
        PluginLog.Debug($"[Notification] {type}:{content}");
        Dalamud.api.ShowNotification(content, type, 5000);
    }

    public static void PushStyleColors(bool pushNew, uint color, params ImGuiCol[] colors)
    {
        if (pushNew)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                ImGui.PushStyleColor(colors[i], color);
            }
        }
        else
        {
            for (int i = 0; i < colors.Length; i++)
            {
                ImGui.PushStyleColor(colors[i], ImGui.GetColorU32(colors[i]));
            }
        }
    }

    public static void PushStyleColors(bool pushNew, Vector4 color, params ImGuiCol[] colors)
    {
        if (pushNew)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                ImGui.PushStyleColor(colors[i], color);
            }
        }
        else
        {
            for (int i = 0; i < colors.Length; i++)
            {
                ImGui.PushStyleColor(colors[i], ImGui.GetColorU32(colors[i]));
            }
        }
    }

    public static bool InputIntWithReset(string label, ref int num, int step, Func<int> getDefaultValue)
    {
        var b = ImGui.InputInt(label, ref num, step);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            num = getDefaultValue();
            b = true;
        }

        return b;
    }

    public static float GetWindowContentRegionWidth() => ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

    public static float GetWindowContentRegionHeight() => ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y;

    public static Vector2 GetWindowContentRegion() => ImGui.GetWindowContentRegionMax() - ImGui.GetWindowContentRegionMin();

    //https://github.com/UnknownX7/DalamudRepoBrowser/blob/master/PluginUI.cs#L20
    public static bool AddHeaderIcon(string id, string icon, string tooltip = null)
    {
        if (ImGui.IsWindowCollapsed()) return false;
        var nodeco = ImGui.GetWindowContentRegionMin() == ImGui.GetStyle().WindowPadding;
        var prevCursorPos = ImGui.GetCursorPos();
        var height = ImGui.GetTextLineHeightWithSpacing() * 0.95f;
        var textLineHeight = new Vector2(height);
        var buttonPos = new Vector2(ImGui.GetWindowWidth() - (nodeco ? 1.05f : 2.85f) * height, (ImGui.GetFrameHeight() - height) / 2);
        ImGui.SetCursorPos(buttonPos);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRectFullScreen();

        var pressed = false;
        ImGui.InvisibleButton(id, textLineHeight);
        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        var halfSize = ImGui.GetItemRectSize() / 2;
        var center = itemMin + halfSize;
        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(itemMin, itemMax, false))
        {
            ImGui.GetWindowDrawList().AddCircleFilled(center, halfSize.X, ImGui.GetColorU32(ImGui.IsMouseDown(ImGuiMouseButton.Left) ? ImGuiCol.ButtonActive : ImGuiCol.ButtonHovered));
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                pressed = true;

            if (tooltip != null)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(tooltip);
                ImGui.EndTooltip();
            }
        }

        ImGui.SetCursorPos(buttonPos);
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), center - ImGui.CalcTextSize(icon) / 2, ImGui.GetColorU32(ImGuiCol.Text), icon);
        ImGui.PopFont();

        ImGui.PopClipRect();
        ImGui.SetCursorPos(prevCursorPos);

        return pressed;
    }

    public static void TextCopyable(string text)
    {
        ImGui.TextUnformatted(text);

        if (!ImGui.IsItemHovered()) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked())
        {
            ImGui.SetClipboardText(text);
            ImGuiUtil.AddNotification(NotificationType.Info, "Copied to clipboard");
        }
    }

    public static void DrawFontawesomeIconOutlined(FontAwesomeIcon icon, Vector4 outline, Vector4 iconColor)
    {
        var positionOffset = ImGuiHelpers.ScaledVector2(0.0f, 1.0f);
        var cursorStart = ImGui.GetCursorPos() + positionOffset;
        ImGui.PushFont(UiBuilder.IconFont);

        ImGui.PushStyleColor(ImGuiCol.Text, outline);
        foreach (var x in Enumerable.Range(-1, 3))
        {
            foreach (var y in Enumerable.Range(-1, 3))
            {
                if (x is 0 && y is 0) continue;

                ImGui.SetCursorPos(cursorStart + new Vector2(x, y));
                ImGui.Text(icon.ToIconString());
            }
        }

        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, iconColor);
        ImGui.SetCursorPos(cursorStart);
        ImGui.Text(icon.ToIconString());
        ImGui.PopStyleColor();

        ImGui.PopFont();

        ImGui.SetCursorPos(ImGui.GetCursorPos() - positionOffset);
    }

    //https://git.annaclemens.io/ascclemens/ChatTwo/src/commit/b63d007f15a825b669523a78945dc872e663c348/ChatTwo/Util/ImGuiUtil.cs#L215
    internal static bool BeginComboVertical(string label, string previewValue, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        return ImGui.BeginCombo($"##{label}", previewValue, flags);
    }

    internal static bool DragFloatVertical(string label, ref float value, float vSpeed = 1.0f, float vMin = float.MinValue, float vMax = float.MaxValue, string? format = null, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        return ImGui.DragFloat($"##{label}", ref value, vSpeed, vMin, vMax, format, flags);
    }

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]

    public static extern unsafe void igClearActiveID();
}
