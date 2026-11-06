using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>仅记录浏览位置，不保存或修改平台游戏状态。</summary>
    [DataContract]
    public sealed class NavigationPreference
    {
        [DataMember] public int schema = 1;
        [DataMember] public string provider, sourceInstance, itemId;
        [DataMember] public bool showAll;
        [DataMember] public string filter, platform, query;
        [DataMember] public bool sortByName;
        [DataMember] public double scrollOffset;
        /// <summary>旧文件或损坏文件回退到首页，保留原文件用于恢复。</summary>
        public static NavigationPreference Load(string root)
        {
            try { var value = ThemeComposer.Read<NavigationPreference>(Path.Combine(root,"navigation.json")); return value != null && value.schema == 1 ? value : new NavigationPreference(); }
            catch { return new NavigationPreference(); }
        }
        /// <summary>保存完整内容命名空间和页面，原子替换并保留上一次位置。</summary>
        public void Save(string root)
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root,"navigation.json"), temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) new DataContractJsonSerializer(typeof(NavigationPreference)).WriteObject(stream,this);
                if (File.Exists(file)) File.Replace(temporary,file,file+".bak"); else File.Move(temporary,file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
