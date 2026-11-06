using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace ModularShell.PlayniteIntegration
{
    [DataContract]
    public sealed class ThemeMetrics
    {
        [DataMember] public int schema;
        [DataMember] public double safeMargin;
        [DataMember] public double cardWidth;
        [DataMember] public double titleSize;
        [DataMember] public double videoFadeMs;
    }
    [DataContract]
    public sealed class PaletteDefinition
    {
        [DataMember] public int schema;
        [DataMember] public string id;
        [DataMember] public string surface;
        [DataMember] public string card;
        [DataMember] public string accent;
        [DataMember] public string text;
        [DataMember] public string muted;
    }
    [DataContract]
    public sealed class ThemePreference
    {
        [DataMember] public int schema = 1;
        [DataMember] public string palette = "midnight";
        [DataMember] public string layout = "ps5ish";
        [DataMember] public string motion = "soft";
        [DataMember] public string ambience = "ocean";
    }

    /// <summary>统一组合可信布局与数据配色；失败时保持上一个有效组合。</summary>
    public static class ThemeComposer
    {
        /// <summary>读取指定 JSON 模型，不解释脚本或 CLR 类型。</summary>
        public static T Read<T>(string file)
        {
            using (var stream = File.OpenRead(file)) {
                if(stream.Length>65536)throw new InvalidDataException("主题配置超过 64 KiB 限额");
                using(var reader=new StreamReader(stream))
                using(var json=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd())))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(json);
            }
        }

        /// <summary>读取用户预设；仅允许内置的已知配色 ID，损坏时安全回退。</summary>
        public static string ReadPreference(string dataRoot)
        {
            try
            {
                var value = Read<ThemePreference>(Path.Combine(dataRoot, "theme.json"));
                return value.schema == 1 && value.palette == "ember" ? "ember" : "midnight";
            }
            catch { return "midnight"; }
        }

        /// <summary>旧 schema 1 预设缺少布局时兼容默认布局，非法 ID 不参与路径拼接。</summary>
        public static string ReadLayoutPreference(string dataRoot)
        {
            try
            {
                var value = Read<ThemePreference>(Path.Combine(dataRoot, "theme.json"));
                return value.schema == 1 && value.layout == "cinema" ? "cinema" : "ps5ish";
            }
            catch { return "ps5ish"; }
        }

        /// <summary>设置保存到 SDK 用户目录，先写临时文件再原子替换，保留上一份。</summary>
        public static void SavePreference(string dataRoot, string palette)
        {
            SavePreference(dataRoot, palette, ReadLayoutPreference(dataRoot));
        }

        /// <summary>从设置页保存布局和配色组合；先校验全部标识再原子提交。</summary>
        public static void SavePreference(string dataRoot, string palette, string layoutId)
        {
            var current = ReadCombination(dataRoot);
            SaveCombination(dataRoot, new ThemePreference { palette = palette, layout = layoutId, motion = current.motion, ambience = current.ambience });
        }

        /// <summary>旧配置缺字段时补默认包，不修改内容级素材。</summary>
        public static ThemePreference ReadCombination(string dataRoot)
        {
            try {
                var value = Read<ThemePreference>(Path.Combine(dataRoot, "theme.json"));
                if (value.schema != 1) throw new InvalidDataException();
                value.motion = value.motion == "brisk" ? "brisk" : "soft";
                value.ambience = value.ambience == "dusk" ? "dusk" : "ocean";
                return value;
            } catch { return new ThemePreference(); }
        }

        /// <summary>组合配置整体校验后原子保存，换配色或布局保留动效与资产选择。</summary>
        public static void SaveCombination(string dataRoot, ThemePreference preference)
        {
            string palette = preference.palette, layoutId = preference.layout;
            if (palette != "midnight" && palette != "ember") throw new InvalidDataException("未知配色");
            if (layoutId != "ps5ish" && layoutId != "cinema") throw new InvalidDataException("未知布局");
            if (preference.schema != 1 || (preference.motion != "soft" && preference.motion != "brisk") || (preference.ambience != "ocean" && preference.ambience != "dusk")) throw new InvalidDataException("未知组合包");
            Directory.CreateDirectory(dataRoot);
            string file = Path.Combine(dataRoot, "theme.json");
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary))
                    new DataContractJsonSerializer(typeof(ThemePreference)).WriteObject(stream, preference);
                if (File.Exists(file)) File.Replace(temporary, file, file + ".bak"); else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>完整校验画刷后一次提交，缺失字段不会造成半套配色。</summary>
        public static void ApplyPalette(FrameworkElement view, string root, string id)
        {
            if (id != "midnight" && id != "ember") throw new InvalidDataException("未知配色");
            var value = Read<PaletteDefinition>(Path.Combine(root, "themes", "palette." + id + ".json"));
            if (value == null || value.schema != 1 || value.id != id) throw new InvalidDataException("配色契约不兼容");
            string[] keys = { "Surface", "Card", "Accent", "Text", "Muted" };
            string[] colors = { value.surface, value.card, value.accent, value.text, value.muted };
            var dictionary = new ResourceDictionary();
            for (int i = 0; i < keys.Length; i++)
            {
                if (String.IsNullOrWhiteSpace(colors[i])) throw new InvalidDataException("缺少画刷 " + keys[i]);
                dictionary[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            }
            // 与直接逐项覆盖相比，替换独立字典不会留下半更新的主题。
            var previous = view.Resources["__palette"] as ResourceDictionary;
            if (previous != null) view.Resources.MergedDictionaries.Remove(previous);
            view.Resources.MergedDictionaries.Add(dictionary);
            view.Resources["__palette"] = dictionary;
        }

        /// <summary>载入外部可信布局；缺控件或语法错误时使用程序集内安全副本。</summary>
        public static FrameworkElement Load(string root, bool safe, out string diagnostic)
        {
            return Load(root, safe, "ps5ish", out diagnostic);
        }

        /// <summary>布局结构与配色独立选择；安全模式始终载入内置默认结构。</summary>
        public static FrameworkElement Load(string root, bool safe, string layoutId, out string diagnostic)
        {
            diagnostic = null;
            FrameworkElement view = null;
            if (!safe)
            {
                try
                {
                    if (layoutId != "ps5ish" && layoutId != "cinema") throw new InvalidDataException("未知布局");
                    using (var stream = File.OpenRead(Path.Combine(root, "themes", "layout." + layoutId + ".xaml"))) view = (FrameworkElement)XamlReader.Load(stream);
                    Validate(view);
                }
                catch (Exception ex) { diagnostic = "外部布局未应用，已使用安全布局：" + ex.Message; view = null; }
            }
            if (view == null)
                using (var stream = typeof(ThemeComposer).Assembly.GetManifestResourceStream("SafeLayout.xaml")) view = (FrameworkElement)XamlReader.Load(stream);
            // 基础画刷总是存在；外部配色只进入受控的合并字典。
            var defaults = new ResourceDictionary();
            defaults["Surface"] = new SolidColorBrush(Color.FromRgb(16, 27, 50));
            defaults["Card"] = new SolidColorBrush(Color.FromRgb(36, 69, 102));
            defaults["Accent"] = Brushes.LightSkyBlue; defaults["Text"] = Brushes.White; defaults["Muted"] = Brushes.LightGray;
            view.Resources.MergedDictionaries.Add(defaults);
            ResourceDictionary components = null;
            if (!safe) try
            {
                using (var stream = File.OpenRead(Path.Combine(root, "themes", "components.controls.xaml"))) components = (ResourceDictionary)XamlReader.Load(stream);
                ValidateComponents(components);
            }
            catch (Exception ex) { diagnostic = "组件已回退：" + ex.Message; components = null; }
            if (components == null)
                using (var stream = typeof(ThemeComposer).Assembly.GetManifestResourceStream("SafeComponents.xaml")) components = (ResourceDictionary)XamlReader.Load(stream);
            view.Resources.MergedDictionaries.Add(components);
            // 旧组件包仍可使用，只为缺失的服务图标槽位补入内置模板。
            if (!components.Contains("MediaServiceStyle") || !components.Contains("MediaServiceTemplate")) {
                ResourceDictionary fallback;
                using(var stream=typeof(ThemeComposer).Assembly.GetManifestResourceStream("SafeComponents.xaml")) fallback=(ResourceDictionary)XamlReader.Load(stream);
                view.Resources["MediaServiceStyle"]=fallback["MediaServiceStyle"];
                view.Resources["MediaServiceTemplate"]=fallback["MediaServiceTemplate"];
            }
            // 设计变量独立于代码组件和配色；缺失时保留内置舒适尺度。
            ApplyMetrics(view, new ThemeMetrics { schema = 1, safeMargin = 48, cardWidth = 154, titleSize = 36, videoFadeMs = 400 });
            if (!safe) try { ApplyMetrics(view, Read<ThemeMetrics>(Path.Combine(root, "themes", "metrics.comfort.json"))); }
            catch (Exception ex) { diagnostic = "设计变量已回退：" + ex.Message; }
            return view;
        }

        /// <summary>只接受版本约定的明确样式槽位，不允许组件包扩散全局覆盖。</summary>
        private static void ValidateComponents(ResourceDictionary value)
        {
            if ((value.Count != 3 && value.Count != 5 && value.Count != 7 && value.Count != 9) || value.MergedDictionaries.Count != 0) throw new InvalidDataException("组件资源范围不兼容");
            // 新版只增加两个已知视觉资源，仍禁止任意全局样式扩散。
            if(value.Count>=5&&(!(value["Ps5ShellSettingRowStyle"] is Style)||!(value["Ps5FocusBrush"] is Brush)))throw new InvalidDataException("PS5 组件资源类型不兼容");
            // 新增搜索与圆形工具按钮两个有名槽位，仍拒绝任意全局资源扩散。
            if(value.Count>=7){
                var search=value["Ps5SearchBoxStyle"] as Style;var circle=value["Ps5CircleButtonStyle"] as Style;
                if(search==null||search.TargetType!=typeof(TextBox)||circle==null||circle.TargetType!=typeof(Button))throw new InvalidDataException("搜索与圆形按钮组件类型不兼容");
            }
            if(value.Count==9) {
                var service=value["MediaServiceStyle"] as Style;
                if(service==null || service.TargetType!=typeof(ListBoxItem) || !(value["MediaServiceTemplate"] is DataTemplate))throw new InvalidDataException("服务图标组件类型不兼容");
            }
            foreach (string key in new[] { "ActionStyle", "NavigationStyle", "OptionStyle" })
            {
                var style = value[key] as Style;
                if (style == null || style.TargetType != (key == "OptionStyle" ? typeof(CheckBox) : typeof(Button))) throw new InvalidDataException("组件类型错误：" + key);
            }
        }

        /// <summary>完整验证可调尺度，避免损坏配置把焦点或按钮移出画布。</summary>
        public static void ApplyMetrics(FrameworkElement view, ThemeMetrics metrics)
        {
            if (metrics == null || metrics.schema != 1 || !InRange(metrics.safeMargin, 24, 80) || !InRange(metrics.cardWidth, 120, 200)
                || !InRange(metrics.titleSize, 28, 44) || !InRange(metrics.videoFadeMs, 0, 1000)) throw new InvalidDataException("设计变量超出契约范围");
            view.Resources["SafeMargin"] = new Thickness(metrics.safeMargin, 30, metrics.safeMargin, 30);
            view.Resources["CardWidth"] = metrics.cardWidth; view.Resources["TitleSize"] = metrics.titleSize; view.Resources["VideoFadeMs"] = metrics.videoFadeMs;
        }
        /// <summary>显式排除 NaN 和无穷值，保障尺寸及动画时间合法。</summary>
        private static bool InRange(double value, double min, double max) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= min && value <= max; }

        /// <summary>槽位必须存在且类型兼容，错误包在应用前拒绝。</summary>
        public static void Validate(FrameworkElement view)
        {
            if (view != null)
            {
                // 动态游戏库需要可重挂的面板；旧布局不兼容时在组合阶段回退。
                var gameList=view.FindName("Games") as ListBox;
                var back=view.FindName("BackDetails") as Button;
                var save=view.FindName("SaveSettings") as Button;
                if(!(view is Panel)||gameList==null||!(gameList.Parent is Grid)||back==null||!(back.Parent is Panel)
                    ||save==null||!(save.Parent is Panel)||!(((Panel)save.Parent).Parent is Panel))throw new InvalidDataException("布局页面容器不兼容");
                foreach (string name in new[] { "Home", "Library", "Settings", "Details", "SaveSettings", "CancelSettings", "BackDetails" })
                    if (!(view.FindName(name) is Button)) throw new InvalidDataException("缺少操作槽位 " + name);
                foreach (string name in new[] { "OptionAutoVideo", "OptionMusic", "OptionTrailerAudio", "OptionReducedMotion", "OptionNightMode", "OptionController" })
                    if (!(view.FindName(name) is CheckBox)) throw new InvalidDataException("缺少设置槽位 " + name);
                if (!(view.FindName("SettingsPage") is Grid) || !(view.FindName("DetailsPage") is Grid)
                    || !(view.FindName("SettingsError") is TextBlock) || !(view.FindName("PluginDiagnostic") is TextBlock)
                    || !(view.FindName("DetailTitle") is TextBlock) || !(view.FindName("DetailInfo") is TextBlock)) throw new InvalidDataException("缺少页面槽位");
            }
            if (view == null || !(view.FindName("Games") is ListBox) || !(view.FindName("Scene") is Image)
                || !(view.FindName("VideoLayer") is Image) || !(view.FindName("Chrome") is Grid)
                || !(view.FindName("TrailerHints") is TextBlock) || !(view.FindName("Trailer") is Button)
                || !(view.FindName("Title") is TextBlock) || !(view.FindName("Metadata") is TextBlock)
                || !(view.FindName("Status") is TextBlock) || !(view.FindName("Sections") is StackPanel)
                || !(view.FindName("Start") is Button) || !(view.FindName("Palette") is Button))
                throw new InvalidDataException("布局缺少契约 2 的必要槽位");
        }
    }
}
