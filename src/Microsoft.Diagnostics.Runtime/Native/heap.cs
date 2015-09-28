﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Diagnostics.Runtime.Desktop;
using System;
using System.Collections.Generic;
using System.IO;
using Address = System.UInt64;

namespace Microsoft.Diagnostics.Runtime.Native
{
    internal class NativeHeap : HeapBase
    {
        internal NativeRuntime NativeRuntime { get; set; }
        internal TextWriter Log { get; set; }
        private ulong _lastObj;
        private ClrType _lastType;
        private Dictionary<ulong, int> _indices = new Dictionary<ulong, int>();
        private List<NativeType> _types = new List<NativeType>(1024);
        private NativeModule[] _modules;
        private NativeModule _mrtModule;
        private NativeType _free;

        internal NativeHeap(NativeRuntime runtime, NativeModule[] modules, TextWriter log)
            : base(runtime)
        {
            Log = log;
            NativeRuntime = runtime;
            _modules = modules;
            _mrtModule = FindMrtModule();

            CreateFreeType();
            InitSegments(runtime);
        }

        public override ClrRuntime GetRuntime() { return NativeRuntime; }

        public override int TypeIndexLimit
        {
            get { return _types.Count; }
        }

        public override ClrType GetTypeByIndex(int index)
        {
            return _types[index];
        }

        private NativeModule FindMrtModule()
        {
            foreach (NativeModule module in _modules)
                if (string.Compare(module.Name, "mrt100", StringComparison.CurrentCultureIgnoreCase) == 0 ||
                    string.Compare(module.Name, "mrt100_app", StringComparison.CurrentCultureIgnoreCase) == 0)
                    return module;

            return null;
        }

        private void CreateFreeType()
        {
            ulong free = NativeRuntime.GetFreeType();
            IMethodTableData mtData = NativeRuntime.GetMethodTableData(free);
            _free = new NativeType(this, _types.Count, _mrtModule, "Free", free, mtData);
            _indices[free] = _types.Count;
            _types.Add(_free);
        }

        public override ClrType GetObjectType(ulong objRef)
        {
            ulong eeType;

            if (_lastObj == objRef)
                return _lastType;

            var cache = MemoryReader;
            if (!cache.Contains(objRef))
                cache = NativeRuntime.MemoryReader;

            if (!cache.ReadPtr(objRef, out eeType))
                return null;

            if ((((int)eeType) & 3) != 0)
                eeType &= ~3UL;

            ClrType last = null;
            int index;
            if (_indices.TryGetValue(eeType, out index))
                last = _types[index];
            else
                last = ConstructObjectType(eeType);

            _lastObj = objRef;
            _lastType = last;
            return last;
        }

        private ClrType ConstructObjectType(ulong eeType)
        {
            IMethodTableData mtData = NativeRuntime.GetMethodTableData(eeType);
            if (mtData == null)
                return null;

            ulong componentType = mtData.ElementTypeHandle;
            bool isArray = componentType != 0;

            // EEClass is the canonical method table.  I stuffed the pointer there instead of creating a new property.
            ulong canonType = isArray ? componentType : mtData.EEClass;
            if (!isArray && canonType != 0)
            {
                int index;
                if (!isArray && _indices.TryGetValue(canonType, out index))
                {
                    _indices[eeType] = index;  // Link the original eeType to its canonical GCHeapType.
                    return _types[index];
                }

                ulong tmp = eeType;
                eeType = canonType;
                canonType = tmp;
            }

            // TODO:  NativeRuntime needs to resolve addresses into eetype names.
            string name = string.Format("type names not impl {0:x}", eeType);

            int len = name.Length;
            if (name.EndsWith("::`vftable'"))
                len -= 11;

            int i = name.IndexOf('!') + 1;
            name = name.Substring(i, len - i);

            if (isArray)
                name += "[]";

            NativeModule module = FindContainingModule(eeType);
            if (module == null && canonType != 0)
                module = FindContainingModule(canonType);

            if (module == null)
                module = _mrtModule;

            NativeType type = new NativeType(this, _types.Count, module, name, eeType, mtData);
            _indices[eeType] = _types.Count;
            if (!isArray)
                _indices[canonType] = _types.Count;
            _types.Add(type);

            return type;
        }

        private NativeModule FindContainingModule(Address eeType)
        {
            int min = 0, max = _modules.Length;

            while (min <= max)
            {
                int mid = (min + max) / 2;

                int compare = _modules[mid].ComparePointer(eeType);
                if (compare < 0)
                    max = mid - 1;
                else if (compare > 0)
                    min = mid + 1;
                else
                    return _modules[mid];
            }

            return null;
        }

        public override IEnumerable<ClrRoot> EnumerateRoots()
        {
            return EnumerateRoots(true);
        }


        public override IEnumerable<ClrRoot> EnumerateRoots(bool enumerateStatics)
        {
            // Stack objects.
            foreach (var thread in NativeRuntime.Threads)
                foreach (var stackRef in NativeRuntime.EnumerateStackRoots(thread))
                    yield return stackRef;

            // Static Variables.
            foreach (var root in NativeRuntime.EnumerateStaticRoots(enumerateStatics))
                yield return root;

            // Handle Table.
            foreach (ClrRoot root in NativeRuntime.EnumerateHandleRoots())
                yield return root;

            // Finalizer Queue.
            ClrAppDomain domain = NativeRuntime.AppDomains[0];
            foreach (ulong obj in NativeRuntime.EnumerateFinalizerQueue())
            {
                ClrType type = GetObjectType(obj);
                if (type == null)
                    continue;

                yield return new NativeFinalizerRoot(obj, type, domain, "finalizer root");
            }
        }

        public override int ReadMemory(Address address, byte[] buffer, int offset, int count)
        {
            if (offset != 0)
                throw new NotImplementedException("Non-zero offsets not supported (yet)");

            int bytesRead = 0;
            if (!NativeRuntime.ReadMemory(address, buffer, count, out bytesRead))
                return 0;
            return bytesRead;
        }

        public override IEnumerable<ClrType> EnumerateTypes() { return null; }
        public override IEnumerable<Address> EnumerateFinalizableObjects() { throw new NotImplementedException(); }
        public override IEnumerable<BlockingObject> EnumerateBlockingObjects() { throw new NotImplementedException(); }
        public override ClrException GetExceptionObject(Address objRef) { throw new NotImplementedException(); }

        protected override int GetRuntimeRevision()
        {
            return 0;
        }
    }


    internal class NativeFinalizerRoot : ClrRoot
    {
        private string _name;
        private ClrType _type;
        private ClrAppDomain _appDomain;

        public override GCRootKind Kind
        {
            get { return GCRootKind.Finalizer; }
        }

        public override ClrType Type
        {
            get { return _type; }
        }

        public override string Name
        {
            get
            {
                return _name;
            }
        }

        public override ClrAppDomain AppDomain
        {
            get
            {
                return _appDomain;
            }
        }

        public NativeFinalizerRoot(Address obj, ClrType type, ClrAppDomain domain, string name)
        {
            Object = obj;
            _name = name;
            _type = type;
            _appDomain = domain;
        }
    }
}