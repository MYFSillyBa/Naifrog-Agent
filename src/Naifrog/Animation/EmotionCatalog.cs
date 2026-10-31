using System;
using System.Collections.Generic;

namespace Naifrog.Animation;

public record ExpressionDefinition(string Name, string Label, string Gesture, int Priority, bool Automatic = true);

public static class EmotionCatalog
{
    public static IReadOnlyList<ExpressionDefinition> Entries { get; } = Array.AsReadOnly(new[]
    {
        new ExpressionDefinition("happy", "开心", "bounce", 30),
        new ExpressionDefinition("surprised", "惊讶", "recoil", 30),
        new ExpressionDefinition("confused", "困惑", "tilt", 20),
        new ExpressionDefinition("shy", "害羞", "sway", 25),
        new ExpressionDefinition("sad", "难过", "sigh", 25),
        new ExpressionDefinition("angry", "生气", "tremble", 30),
        new ExpressionDefinition("love", "喜欢", "sway", 25),
        new ExpressionDefinition("proud", "骄傲", "rise", 25),
        new ExpressionDefinition("cheer", "加油", "bounce", 30),
        new ExpressionDefinition("celebrate", "庆祝", "bounce", 35),
        new ExpressionDefinition("greet", "打招呼", "sway", 25),
        new ExpressionDefinition("grateful", "感谢", "bow", 25),
        new ExpressionDefinition("agree", "赞同", "nod", 20),
        new ExpressionDefinition("refuse", "拒绝", "shake", 25),
        new ExpressionDefinition("ponder", "思考", "tilt", 20),
        new ExpressionDefinition("curious", "好奇", "tilt", 20),
        new ExpressionDefinition("scared", "害怕", "tremble", 30),
        new ExpressionDefinition("worried", "担心", "sway", 25),
        new ExpressionDefinition("crying", "哭泣", "tremble", 30),
        new ExpressionDefinition("wronged", "委屈", "sigh", 25),
        new ExpressionDefinition("disappointed", "失落", "sigh", 25),
        new ExpressionDefinition("embarrassed", "尴尬", "sway", 25),
        new ExpressionDefinition("sleepy", "困倦", "sigh", 20),
        new ExpressionDefinition("annoyed", "烦恼", "shake", 25),
        new ExpressionDefinition("relieved", "释然", "breathe", 20),
        new ExpressionDefinition("hug", "抱抱", "sway", 25),
        new ExpressionDefinition("smile", "微笑", "breathe", 20, false),
        new ExpressionDefinition("excited", "兴奋", "bounce", 30, false),
        new ExpressionDefinition("focused", "专注", "breathe", 35, false),
        new ExpressionDefinition("bored", "无聊", "sigh", 20, false),
        new ExpressionDefinition("smug", "得意", "rise", 25, false),
        new ExpressionDefinition("dizzy", "晕乎", "sway", 25, false),
        new ExpressionDefinition("shocked", "震惊", "recoil", 35, false),
        new ExpressionDefinition("organize", "整理文件", "bow", 45, false),
        // Local business actions, outside the upstream expression catalog.
        new ExpressionDefinition("eat", "吃点东西（本地动作）", "squat", 40, false),
        new ExpressionDefinition("walk", "散步（本地动作）", "sway", 40, false)
    });

    internal static ExpressionDefinition? Find(string name)
    {
        foreach (var entry in Entries)
            if (string.Equals(entry.Name, name, StringComparison.Ordinal)) return entry;
        return null;
    }
}
