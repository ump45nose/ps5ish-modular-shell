using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>消费场景设置独立保存，不改变主题组合或内容级素材选择。</summary>
    [DataContract]
    public sealed class ShellSettings
    {
        [DataMember] public int schema = 1;
        [DataMember] public bool autoVideo = true;
        [DataMember] public bool music = true;
        [DataMember] public bool trailerAudio = true;
        [DataMember] public bool reducedMotion;
        [DataMember] public bool nightMode;
        [DataMember] public bool controller = true;
        [DataMember] public bool uiSounds = true;
        [DataMember] public double musicVolume = .35, soundVolume = .35;
        [DataMember] public string backgroundMusic;
        [DataMember] public bool startupAnimation = true;
        [DataMember] public string startupVideo;
        /// <summary>旧配置缺字段时采用安全的产品默认值。</summary>
        [OnDeserializing]
        private void Defaults(StreamingContext context) { schema = 1; autoVideo = true; music = true; trailerAudio=true; controller = true;uiSounds=true;musicVolume=.35;soundVolume=.35;startupAnimation=true; }
        /// <summary>损坏或不兼容设置回退；不覆盖用户原文件。</summary>
        public static ShellSettings Load(string root)
        {
            try { var settings = ThemeComposer.Read<ShellSettings>(Path.Combine(root, "preview.json")); return settings.schema == 1 ? settings : new ShellSettings(); }
            catch { return new ShellSettings(); }
        }
        /// <summary>先完整落盘再替换，并保留上一份设置供回退。</summary>
        public void Save(string root)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "preview.json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = File.Create(temporary)) new DataContractJsonSerializer(typeof(ShellSettings)).WriteObject(file, this);
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
