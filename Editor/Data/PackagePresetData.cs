using System;
using System.Collections.Generic;

namespace PackageSetupTool
{
    // 새 프로젝트에 적용할 패키지 묶음 하나. 표시명까지 복사해 두어 카탈로그 없이도 표시·적용할 수 있다.
    // 공용 폴더의 JSON 파일(<공용 폴더>/Presets/<이름>.json)로 저장한다. 파일 형식은 {"packages":[...]} 이다.
    [Serializable]
    public class PackagePresetData
    {
        // 파일 이름에서 정해지므로 JSON 에는 넣지 않는다.
        [NonSerialized] public string name;
        public List<PackageEntry> packages = new();

        public bool Contains(PackageType type, string id)
        {
            return packages.Exists(x => x.HasKey(type, id));
        }

        public List<PackageEntry> GetPackages(PackageType type)
        {
            return packages.FindAll(x => x.type == type);
        }

        public void SetIncluded(PackageEntry entry, bool isIncluded)
        {
            packages.RemoveAll(x => x.HasKey(entry.type, entry.id));
            if (isIncluded)
            {
                packages.Add(entry.Clone());
            }
        }
    }
}
