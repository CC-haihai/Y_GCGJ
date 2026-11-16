using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace VpLayer
{
    /// <summary>
    /// 视口标签：把「视口句柄 → 归层配置名」写进图纸里。
    ///
    /// 存哪儿：图纸级命名对象字典（Named Objects Dictionary）里的一条 Xrecord。
    ///   - 不是每个视口一条 —— 50 个视口也只有一条记录，
    ///     体积约 100 字节 + 每视口 20 字节，对几 MB 的 DWG 可以忽略。
    ///   - Xrecord 不出现在 LIST / 特性面板 / 打印里，也没生成任何实体或图层，
    ///     没装本插件的人打开图纸完全无从得知。
    ///
    /// 为什么不用 XData：XData 单实体上限 16KB，还要注册 RegApp；
    /// Xrecord 没这些约束，读写也更干净。
    ///
    /// 数据格式：TypedValue 平铺，第 0 位是标记串，之后两两一组（句柄、配置名）。
    /// 用交替位置而不是 "句柄=配置名" 拼串，省掉转义与分隔符的坑。
    /// </summary>
    public static class VpTagStore
    {
        public const string DictKey = "AUTOLAYER_VP_TAG";
        const string Marker = "AUTOLAYER_VP_TAG v1";

        // ------------------------------------------------------------------ 读

        /// <summary>整张图纸的标签：句柄 → 配置名。永远不返回 null。</summary>
        public static Dictionary<string, string> Read(Database db)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (db == null) return map;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ReadIn(tr, db, map);
                    tr.Commit();
                }
            }
            catch { }

            return map;
        }

        /// <summary>事务内版本，供外层已有事务时复用（AutoCAD 不支持嵌套事务）。</summary>
        public static void ReadIn(Transaction tr, Database db, Dictionary<string, string> map)
        {
            if (map == null) return;

            Xrecord rec = OpenRecord(tr, db, false);
            if (rec == null) return;

            ResultBuffer rb = rec.Data;
            if (rb == null) return;

            TypedValue[] vals = rb.AsArray();
            rb.Dispose();

            if (vals == null || vals.Length < 3) return;
            if (!(vals[0].Value is string) || (string)vals[0].Value != Marker) return;

            for (int i = 1; i + 1 < vals.Length; i += 2)
            {
                string h = vals[i].Value as string;
                string p = vals[i + 1].Value as string;
                if (!string.IsNullOrEmpty(h) && !string.IsNullOrEmpty(p)) map[h] = p;
            }
        }

        /// <summary>某个视口绑的配置名；没标签返回 null。</summary>
        public static string GetProfile(Database db, ObjectId vpId)
        {
            if (db == null || vpId.IsNull) return null;
            string h = HandleOf(db, vpId);
            if (h == null) return null;

            Dictionary<string, string> map = Read(db);
            string p;
            return map.TryGetValue(h, out p) ? p : null;
        }

        // ------------------------------------------------------------------ 写

        /// <summary>给一个视口贴标签。已存在则覆盖。</summary>
        public static bool Set(Database db, ObjectId vpId, string profile)
        {
            string h = HandleOf(db, vpId);
            return h != null && Set(db, h, profile);
        }

        public static bool Set(Database db, string handle, string profile)
        {
            if (db == null || string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(profile)) return false;

            Dictionary<string, string> map = Read(db);
            map[handle] = profile;
            return Write(db, map);
        }

        /// <summary>事务内版本，供批量生成时复用外层事务。</summary>
        public static bool SetIn(Transaction tr, Database db, ObjectId vpId, string profile)
        {
            if (tr == null || db == null || vpId.IsNull || string.IsNullOrEmpty(profile)) return false;

            string h = HandleOf(db, vpId);
            if (h == null) return false;

            try
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ReadIn(tr, db, map);
                map[h] = profile;
                WriteIn(tr, db, map);
                return true;
            }
            catch { return false; }
        }

        public static bool Remove(Database db, ObjectId vpId)
        {
            string h = HandleOf(db, vpId);
            if (h == null) return false;

            Dictionary<string, string> map = Read(db);
            if (!map.Remove(h)) return false;
            return Write(db, map);
        }

        /// <summary>
        /// 把本图所有指向 oldName 的标签改成 newName。返回改了几个。
        /// 主插件里改配置名时调它 —— 否则图纸里的标签会指向一个已经不存在的名字，
        /// 进视口只会得到一句「配置不存在」。
        /// </summary>
        public static int RenameProfile(Database db, string oldName, string newName)
        {
            if (db == null || string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return 0;

            try
            {
                Dictionary<string, string> map = Read(db);
                if (map.Count == 0) return 0;

                int n = 0;
                var keys = new List<string>(map.Keys);

                foreach (string k in keys)
                {
                    if (!string.Equals(map[k], oldName, StringComparison.OrdinalIgnoreCase)) continue;
                    map[k] = newName;
                    n++;
                }

                if (n > 0) Write(db, map);
                return n;
            }
            catch { return 0; }
        }

        /// <summary>清掉整张图纸的标签记录。</summary>
        public static bool Clear(Database db)
        {
            if (db == null) return false;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId nodId = db.NamedObjectsDictionaryId;
                    DBDictionary nod = tr.GetObject(nodId, OpenMode.ForWrite) as DBDictionary;
                    if (nod != null && nod.Contains(DictKey))
                    {
                        Xrecord rec = tr.GetObject(nod.GetAt(DictKey), OpenMode.ForWrite) as Xrecord;
                        try { nod.Remove(DictKey); } catch { }
                        try { if (rec != null) rec.Erase(); } catch { }
                    }
                    tr.Commit();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>清掉已经指向不存在视口的失效条目。返回清掉几条。</summary>
        public static int Prune(Database db)
        {
            if (db == null) return 0;
            try
            {
                Dictionary<string, string> map = Read(db);
                if (map.Count == 0) return 0;

                int dropped = 0;
                var dead = new List<string>();
                foreach (string h in map.Keys)
                {
                    ObjectId id;
                    if (!TryResolve(db, h, out id)) { dead.Add(h); continue; }
                }
                for (int i = 0; i < dead.Count; i++) { map.Remove(dead[i]); dropped++; }

                if (dropped > 0) Write(db, map);
                return dropped;
            }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ 底层

        static bool Write(Database db, Dictionary<string, string> map)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    WriteIn(tr, db, map);
                    tr.Commit();
                }
                return true;
            }
            catch { return false; }
        }

        static void WriteIn(Transaction tr, Database db, Dictionary<string, string> map)
        {
            ObjectId nodId = db.NamedObjectsDictionaryId;
            DBDictionary nod = tr.GetObject(nodId, OpenMode.ForWrite) as DBDictionary;
            if (nod == null) return;

            Xrecord rec = OpenRecord(tr, db, true);

            if (map == null || map.Count == 0)
            {
                // 没有条目就不留空记录 —— 图纸里多一条空 Xrecord 没有意义。
                // Remove 和 Erase 谁先谁后会因版本而异：先 Remove 会把条目摘掉，
                // 再 Erase 一个已经不在字典里的对象会抛异常把整个事务回滚，
                // 所以两步各自吞掉异常，不能让它把写盘带崩。
                if (rec != null)
                {
                    try { nod.Remove(DictKey); } catch { }
                    try { rec.Erase(); } catch { }
                }
                return;
            }

            bool created = false;
            if (rec == null)
            {
                rec = new Xrecord();
                created = true;
            }

            var vals = new List<TypedValue>(1 + map.Count * 2);
            vals.Add(new TypedValue((int)DxfCode.Text, Marker));
            foreach (KeyValuePair<string, string> kv in map)
            {
                vals.Add(new TypedValue((int)DxfCode.Text, kv.Key));
                vals.Add(new TypedValue((int)DxfCode.Text, kv.Value));
            }

            rec.Data = new ResultBuffer(vals.ToArray());

            if (created)
            {
                nod.SetAt(DictKey, rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }
        }

        static Xrecord OpenRecord(Transaction tr, Database db, bool forWrite)
        {
            try
            {
                ObjectId nodId = db.NamedObjectsDictionaryId;
                DBDictionary nod = tr.GetObject(nodId, forWrite ? OpenMode.ForWrite : OpenMode.ForRead) as DBDictionary;
                if (nod == null || !nod.Contains(DictKey)) return null;

                ObjectId id = nod.GetAt(DictKey);
                if (id.IsNull || !id.IsValid) return null;

                return tr.GetObject(id, forWrite ? OpenMode.ForWrite : OpenMode.ForRead) as Xrecord;
            }
            catch { return null; }
        }

        /// <summary>视口的句柄串（大写十六进制）。拿不到返回 null。</summary>
        static string HandleOf(Database db, ObjectId vpId)
        {
            try
            {
                if (db == null || vpId.IsNull) return null;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBObject o = tr.GetObject(vpId, OpenMode.ForRead);
                    string h = o == null ? null : o.Handle.ToString();
                    tr.Commit();
                    return h;
                }
            }
            catch { return null; }
        }

        static bool TryResolve(Database db, string handle, out ObjectId id)
        {
            id = ObjectId.Null;
            try
            {
                long v = Convert.ToInt64(handle, 16);
                return db.TryGetObjectId(new Handle(v), out id) && !id.IsNull && id.IsValid;
            }
            catch { return false; }
        }

        /// <summary>按句柄找视口实体。面板/命令里点不到视口时用它。</summary>
        public static bool TryGetViewport(Database db, string handle, out ObjectId id)
        {
            id = ObjectId.Null;
            if (db == null || string.IsNullOrEmpty(handle)) return false;
            if (!TryResolve(db, handle, out id)) return false;
            return VpService.IsViewport(id);
        }
    }
}
