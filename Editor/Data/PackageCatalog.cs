using System;
using System.Collections.Generic;

namespace PackageSetupTool
{
    // 목록 수집으로 모은 전체 패키지. 프리셋은 이 목록에서 항목을 골라 담는다.
    // 공용 폴더의 JSON 파일(<공용 폴더>/Catalog.json)로 저장해 같은 PC의 모든 프로젝트가 함께 쓴다.
    [Serializable]
    public class PackageCatalog
    {
        public List<PackageEntry> entries = new();
        public List<PackageDescription> descriptions = new();

        public PackageEntry Find(PackageType type, string id)
        {
            return entries.Find(x => x.HasKey(type, id));
        }

        public List<PackageEntry> GetEntries(PackageType type)
        {
            return entries.FindAll(x => x.type == type);
        }

        // 새로 추가한 항목이면 true. 이미 있으면 표시명만 갱신한다.
        public bool AddOrUpdate(PackageEntry entry)
        {
            var current = Find(entry.type, entry.id);
            if (current == null)
            {
                entries.Add(entry.Clone());
                return true;
            }

            current.displayName = entry.displayName;
            return false;
        }

        public void Remove(PackageType type, string id)
        {
            entries.RemoveAll(x => x.HasKey(type, id));
            descriptions.RemoveAll(x => x.HasKey(type, id));
        }

        // 설명이 없으면 null
        public string GetDescription(PackageType type, string id)
        {
            return descriptions.Find(x => x.HasKey(type, id))?.text;
        }

        public void SetDescription(PackageType type, string id, string text)
        {
            var current = descriptions.Find(x => x.HasKey(type, id));
            if (current == null)
            {
                descriptions.Add(new PackageDescription(type, id, text));
                return;
            }
            current.text = text;
        }
    }
}
