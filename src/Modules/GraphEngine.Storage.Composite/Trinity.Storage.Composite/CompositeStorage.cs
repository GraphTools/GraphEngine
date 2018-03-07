﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Trinity.Diagnostics;
using Trinity.Utilities;

namespace Trinity.Storage.Composite
{
    public static class CompositeStorage
    {
        internal static List<IStorageSchema> s_StorageSchemas;
        internal static List<IGenericCellOperations> s_GenericCellOperations;

        private static Dictionary<string, int> s_CellTypeIDs;
        private static List<int> s_IDIntervals;
        private static List<VersionRecord> s_Versions;
        private static int s_currentCellTypeOffset = 0;

        #region State
        public static int CurrentCellTypeOffset => s_currentCellTypeOffset;
        #endregion

        static CompositeStorage()
        {
            s_IDIntervals = new List<int> { s_currentCellTypeOffset };
            s_StorageSchemas = new List<IStorageSchema>();
            s_CellTypeIDs = new Dictionary<string, int>();
            s_Versions = new List<VersionRecord>();
            s_GenericCellOperations = new List<IGenericCellOperations>();
        }

        public static void LoadMetadata()
        {
            Utils.Session(
                path: PathHelper.Directory,
                start: () => Log.WriteLine($"{nameof(CompositeStorage)}: Loading composite storage extension metadata."),
                err: (e) => Log.WriteLine(LogLevel.Error, $"{nameof(CompositeStorage)}: {{0}}", e.Message),
                end: () => Log.WriteLine($"{nameof(CompositeStorage)}: Successfully loaded composite storage extension metadata."),
                behavior: () =>
                {
                    s_Versions              = Serialization.Deserialize<List<VersionRecord>>(PathHelper.VersionRecorders);
                    s_CellTypeIDs           = Serialization.Deserialize<Dictionary<string, int>>(PathHelper.CellTypeIDs);
                    s_IDIntervals           = Serialization.Deserialize<List<int>>(PathHelper.IDIntervals);
                    var assemblies          = s_Versions.Select(v => $"{v.Namespace}.dll")
                                              .Select(PathHelper.DLL)
                                              .Select(Assembly.LoadFrom)
                                              .ToList();
                    s_StorageSchemas        = assemblies.Select(_ => AssemblyUtility.GetAllClassInstances<IStorageSchema>(assembly: _).First()).ToList();
                    s_GenericCellOperations = assemblies.Select(_ => AssemblyUtility.GetAllClassInstances<IGenericCellOperations>(assembly: _).First()).ToList();
                });
        }

        public static void SaveMetadata()
        {
            Utils.Session(
                path: PathHelper.Directory,
                start: () => Log.WriteLine($"{nameof(CompositeStorage)}: Saving composite storage extension metadata."),
                err: (e) => Log.WriteLine(LogLevel.Error, $"{nameof(CompositeStorage)}: {{0}}", e.Message),
                end: () => Log.WriteLine($"{nameof(CompositeStorage)}: Successfully saved composite storage extension metadata."),
                behavior: () =>
                {
                    Serialization.Serialize(s_Versions, PathHelper.VersionRecorders);
                    Serialization.Serialize(s_IDIntervals, PathHelper.IDIntervals);
                    Serialization.Serialize(s_CellTypeIDs, PathHelper.CellTypeIDs);
                }
            );
        }

        public static void ResetMetadata()
        {
            Log.WriteLine($"{nameof(CompositeStorage)}: Resetting composite storage extension metadata.");
            // TODO
        }


        #region TSL-CodeGen-Build-Load
        private static void CreateCSProj(VersionRecord version)
        {
            var path = Path.Combine(version.TslBuildDir, $"{version.Namespace}.csproj");
            File.WriteAllText(path, CSProj.Template);
        }

        private static bool CodeGen(VersionRecord version)
        {
#if DEBUG
            Directory.GetFiles(version.TslSrcDir, "*.tsl").ToList().ForEach(Console.WriteLine);
#endif

            return Commands.TSLCodeGenCmd(
                    string.Join(" ", Directory.GetFiles(version.TslSrcDir, "*.tsl"))
                    + $" -offset {version.CellTypeOffset} -n {version.Namespace} -o {version.TslBuildDir}");
        }

        private static bool Build(VersionRecord version) =>
            Commands.DotNetBuildCmd($"build {version.TslBuildDir} -o {version.AsmLoadDir}");

        private static Assembly Load(VersionRecord version)
        {
#if DEBUG
            Console.WriteLine("Loading " + Path.Combine(version.AsmLoadDir, $"{version.Namespace}.dll"));
#endif 
            return Assembly.LoadFrom(Path.Combine(version.AsmLoadDir, $"{version.Namespace}.dll"));
        }
        #endregion

        public static void UpdateStorageExtensionSchema(SchemaUpdate changes)
        {

        }

        public static void AddStorageExtension(
                    string tslSrcDir,
                    string tslBuildDir,
                    string moduleName,
                    string versionName = null)
        {

#if DEBUG

            Trinity.Global.StorageSchema
                   .CellDescriptors
                   .Select(cellDesc =>
                            $"{cellDesc.TypeName}: " +
                            $"[{cellDesc.GetFieldNames().By(_ => string.Join(",", _))}]")
                   .ToList()
                   .ForEach(_ => Log.WriteLine(_));
#endif

            string.Join("\n",
                          "Current Storage Info:",
                          $"#VersionRecorders: {s_Versions.Count}",
                          $"#IDIntervals: : {s_IDIntervals.Count}",
                          $"#CellTypeIDs:{s_CellTypeIDs.Count}",
                          $"#StorageSchema:{s_StorageSchemas.Count}",
                          $"#GenericCellOperations:{s_GenericCellOperations.Count}")
                   .By(_ => Log.WriteLine(LogLevel.Debug, $"{nameof(CompositeStorage)}: {{0}}", _));

            var asmLoadDir = PathHelper.Directory;

            asmLoadDir = FileUtility.CompletePath(Path.Combine(asmLoadDir, ""));
            var version = new VersionRecord(
                            s_currentCellTypeOffset,
                            tslSrcDir,
                            tslBuildDir,
                            asmLoadDir,
                            moduleName,
                            versionName ?? DateTime.Now.ToString());

            string.Join("\n",
                          "Meta data paths:",
                          "tslsrcDir: " + version.TslSrcDir,
                          "TslBuildDir: " + version.TslBuildDir,
                          "AsmLoadDir: " + version.AsmLoadDir)
                   .By(_ => Log.WriteLine(LogLevel.Debug, $"{nameof(CompositeStorage)}: {{0}}", _));

            CreateCSProj(version);

            if (!CodeGen(version))
            {
                throw new TSLCodeGenException();
            }

            if (!Build(version))
            {
                throw new TSLBuildException();
            }

            try
            {
                var asm       = Load(version);
                var schema    = AssemblyUtility.GetAllClassInstances<IStorageSchema>(assembly: asm).First();
                var cellOps   = AssemblyUtility.GetAllClassInstances<IGenericCellOperations>(assembly: asm).First();
                var cellDescs = schema.CellDescriptors.ToList();

                s_StorageSchemas.Add(schema);
                s_GenericCellOperations.Add(cellOps);

                int maxoffset = s_currentCellTypeOffset;

                foreach (var cellDesc in cellDescs)
                {
                    s_CellTypeIDs[cellDesc.TypeName] = cellDesc.CellType;
                    // Assertion 1: New cell type does not crash into existing type space
                    Debug.Assert(s_currentCellTypeOffset <= cellDesc.CellType);
                    maxoffset = Math.Max(maxoffset, cellDesc.CellType);

#if DEBUG
                    Console.WriteLine($"{cellDesc.TypeName}{{");
                    foreach(var fieldDesc in cellDesc.GetFieldDescriptors())
                    {    
                        Console.WriteLine($"    {fieldDesc.Name}: {fieldDesc.TypeName}");
                    }
                    Console.WriteLine("}");
#endif
                }


                s_currentCellTypeOffset += cellDescs.Count + 1;

                // Assertion 2: The whole type id space is still compact
                Debug.Assert(s_currentCellTypeOffset == maxoffset + 1);

                s_IDIntervals.Add(s_currentCellTypeOffset);
                s_Versions.Add(version);
                // Assertion 3: intervals grow monotonically
                Debug.Assert(s_IDIntervals.OrderBy(_ => _).SequenceEqual(s_IDIntervals));
            }
            catch (Exception e)
            {
                throw new AsmLoadException(e.Message);
            }
        }


        public static int GetIntervalIndexByCellTypeID(int cellTypeID)
        {
            int seg = s_IDIntervals.FindLastIndex(seg_head => seg_head < cellTypeID);
            if (seg == -1 || seg == s_IDIntervals.Count)
                throw new CellTypeNotMatchException("Cell type id out of the valid range.");
            return seg;
        }

        public static int GetIntervalIndexByCellTypeName(string cellTypeName)
        {
            if (!s_CellTypeIDs.TryGetValue(cellTypeName, out var typeId))
                throw new CellTypeNotMatchException("Unrecognized cell type string.");
            return GetIntervalIndexByCellTypeID(typeId);
        }
    }
}
