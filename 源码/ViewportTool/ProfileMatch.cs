using System;
using System.Collections.Generic;

namespace VpLayer
{
    /// <summary>
    /// 视口状态 ↔ 归层配置的匹配。
    ///
    /// 优先级（Resolve）：
    ///   1. 状态上显式记的绑定名（刷取/标记时写进去的），且配置确实存在 → 用它
    ///   2. 状态名和某个配置名完全一样 → 自动绑上（不用再手动标一次）
    ///   3. 名字接近（差括号、差空格、差一个字）→ 只给建议，不自动用
    ///      —— 自动套一个「差不多」的配置，切错了比不切更难发现
    ///   4. 都没有 → 不绑
    ///
    /// 第 2 条就是「配置名跟状态名一样就自动绑定」；第 3 条是给对齐用的线索。
    /// </summary>
    public static class ProfileMatch
    {
        public enum Kind
        {
            /// <summary>状态上有显式绑定，且配置存在。</summary>
            Bound,

            /// <summary>没有显式绑定，但状态名就是配置名。</summary>
            SameName,

            /// <summary>名字接近某个配置，需要用户确认。</summary>
            Fuzzy,

            /// <summary>完全没有对应关系。</summary>
            Missing
        }

        public sealed class Hit
        {
            public Kind Kind;

            /// <summary>实际要用的配置名。Fuzzy / Missing 时为空 —— 别拿它去切配置。</summary>
            public string Profile = "";

            /// <summary>模糊匹配到的配置名，只用来提示。</summary>
            public string Suggest = "";

            public string Note = "";

            public bool Ok { get { return !string.IsNullOrEmpty(Profile); } }
        }

        /// <summary>算出一个状态该用哪个配置。永不抛异常。</summary>
        public static Hit Resolve(string stateName, string bound)
        {
            var hit = new Hit();

            try
            {
                // 1) 显式绑定
                if (!string.IsNullOrEmpty(bound) && LayerProfile.HasProfile(bound))
                {
                    hit.Kind = Kind.Bound;
                    hit.Profile = bound;
                    return hit;
                }

                // 2) 同名
                if (!string.IsNullOrEmpty(stateName) && LayerProfile.HasProfile(stateName))
                {
                    hit.Kind = Kind.SameName;
                    hit.Profile = stateName;
                    return hit;
                }

                // 3) 接近
                string sug = Suggest(stateName);
                if (!string.IsNullOrEmpty(sug))
                {
                    hit.Kind = Kind.Fuzzy;
                    hit.Suggest = sug;
                    hit.Note = "名字接近配置「" + sug + "」，确认后可以对齐";
                    return hit;
                }

                // 4) 没有
                hit.Kind = Kind.Missing;
                hit.Note = string.IsNullOrEmpty(bound)
                    ? "没有同名配置"
                    : "绑定的配置「" + bound + "」已经不存在了";
                return hit;
            }
            catch
            {
                hit.Kind = Kind.Missing;
                return hit;
            }
        }

        /// <summary>找一个名字最接近的配置名；没有就返回空串。</summary>
        public static string Suggest(string stateName)
        {
            if (string.IsNullOrWhiteSpace(stateName)) return "";

            try
            {
                List<string> names = LayerProfile.ProfileNames();
                if (names.Count == 0) return "";

                string n = Norm(stateName);
                if (n.Length == 0) return "";

                // 归一化后完全一样（「平面图(1)」vs「平面图 1」这类）
                for (int i = 0; i < names.Count; i++)
                    if (Norm(names[i]) == n) return names[i];

                // 互相包含，且短的那个至少两个字 —— 挡住「平面」匹配「平面图」这种太宽的情况
                string best = "";
                for (int i = 0; i < names.Count; i++)
                {
                    string m = Norm(names[i]);
                    if (m.Length < 2 || n.Length < 2) continue;

                    if (n.IndexOf(m, StringComparison.Ordinal) >= 0
                        || m.IndexOf(n, StringComparison.Ordinal) >= 0)
                    {
                        // 长度差最小的那个最像
                        if (best.Length == 0 || Math.Abs(m.Length - n.Length) < Math.Abs(Norm(best).Length - n.Length))
                            best = names[i];
                    }
                }

                if (best.Length > 0) return best;

                // 差一个字（「剖面图」vs「剖面」已经被包含规则吃掉了，这里挡的是同长度的错别字）
                if (n.Length >= 3)
                {
                    for (int i = 0; i < names.Count; i++)
                    {
                        string m = Norm(names[i]);
                        if (Math.Abs(m.Length - n.Length) > 1) continue;
                        if (Distance(n, m) <= 1) return names[i];
                    }
                }
            }
            catch { }

            return "";
        }

        /// <summary>
        /// 归一化：去空白、去括号及括号内容、去结尾序号、全角转半角。
        /// 「平面布置图(1)」「平面布置图 1」「平面布置图（一）」都归成「平面布置图」。
        /// </summary>
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            var buf = new char[s.Length];
            int n = 0;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                // 括号开 → 跳过整段
                if (c == '(' || c == '（' || c == '[' || c == '【' || c == '{')
                {
                    char close = c == '(' ? ')' : c == '（' ? '）' : c == '[' ? ']' : c == '【' ? '】' : '}';
                    int j = s.IndexOf(close, i + 1);
                    if (j > i) { i = j; continue; }
                    continue;
                }

                if (char.IsWhiteSpace(c)) continue;
                if (c == '-' || c == '_' || c == '·' || c == '.') continue;

                // 全角转半角
                if (c >= '０' && c <= '９') c = (char)(c - '０' + '0');
                if (c >= 'Ａ' && c <= 'Ｚ') c = (char)(c - 'Ａ' + 'A');
                if (c >= 'ａ' && c <= 'ｚ') c = (char)(c - 'ａ' + 'a');

                buf[n++] = char.ToLowerInvariant(c);
            }

            // 去掉结尾的数字（「平面图1」「平面图 2」）
            while (n > 0 && buf[n - 1] >= '0' && buf[n - 1] <= '9') n--;

            return new string(buf, 0, n);
        }

        /// <summary>编辑距离，只用来判「差一个字」。</summary>
        static int Distance(string a, string b)
        {
            if (a == b) return 0;
            if (string.IsNullOrEmpty(a)) return b == null ? 0 : b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;

            int[] prev = new int[b.Length + 1];
            int[] cur = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;

                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = prev[j - 1] + cost;
                    if (prev[j] + 1 < v) v = prev[j] + 1;
                    if (cur[j - 1] + 1 < v) v = cur[j - 1] + 1;
                    cur[j] = v;
                }

                int[] t = prev; prev = cur; cur = t;
            }

            return prev[b.Length];
        }
    }
}
