﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Dotx64Dbg
{
    internal class LoaderContext : AssemblyLoadContext
    {
        public Assembly Current;

        public LoaderContext()
            : base(true)
        {
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            Console.WriteLine("LoaderContext.Load({0})", assemblyName.Name);
            return Assembly.Load(assemblyName);
        }

        public Assembly LoadFromFile(string path)
        {
            Current = LoadFromAssemblyPath(path);
            return Current;
        }

        public bool UnloadCurrent()
        {
            if (Current == null)
                return false;

            Unload();
            Current = null;

            return true;
        }

        public bool IsLoaded()
        {
            return Current != null;
        }
    }

    class TransitionContext : IDisposable
    {
        Dictionary<object, object> ReferenceMap = new();
        List<object> NewObjects = new();

        public void Dispose()
        {
            ReferenceMap.Clear();
            ReferenceMap = null;
        }

        public object Create(Type type)
        {
            var obj = FormatterServices.GetUninitializedObject(type);
            NewObjects.Add(obj);
            return obj;
        }

        public bool GetNewReference(object oldObj, out object newObj)
        {
            return ReferenceMap.TryGetValue(oldObj, out newObj);
        }

        public void MapReference(object oldObj, object newObj)
        {
            ReferenceMap.Add(oldObj, newObj);
        }

        public object[] GetObjectsWithInterface(Type type)
        {
            var res = NewObjects.Where(a => a.GetType().GetInterface(type.Name) != null).ToArray();
            return res;
        }
    }

    internal partial class Plugins
    {
        internal bool IsSystemType(Type t)
        {
            return false;
        }

        Type GetPluginClass(Assembly assembly)
        {
            var entries = assembly.GetTypes().Where(a => a.GetInterface("IPlugin") != null).ToArray();
            if (entries.Length > 1)
            {
                throw new Exception("Assembly has multiple classes with IPlugin, can have only one entry.");
            }
            if (entries.Length == 0)
            {
                throw new Exception("Assembly has no IPlugin class.");
            }
            return entries.First();
        }

        void AdaptField(TransitionContext ctx, object oldInstance, FieldInfo oldField, object newInstance, FieldInfo newField)
        {
            var oldValue = oldField.GetValue(oldInstance);
            if (newField.FieldType.IsValueType)
            {
                newField.SetValue(newInstance, oldValue);
            }
            else if (newField.FieldType.IsPrimitive)
            {
                newField.SetValue(newInstance, oldValue);
            }
            else if (newField.FieldType.IsArray)
            {
                object newValue;

                var elemType = newField.FieldType.GetElementType();
                if (elemType.IsValueType)
                {
                    newField.SetValue(newInstance, oldValue);
                }
                else if (elemType.IsClass)
                {
                    throw new Exception("Unsupported state transfer of nested array");
                }
                else
                {
                    // TODO: Iterate and swap everything.
                    if (!ctx.GetNewReference(oldValue, out newValue))
                    {
                        newValue = ctx.Create(newField.FieldType);
                        AdaptInstance(ctx, oldValue, oldField.FieldType, newValue, newField.FieldType);
                    }
                }
            }
            else if (newField.FieldType.IsClass)
            {
                object newValue;

                var fieldType = newField.FieldType;
                if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var listType = fieldType.GenericTypeArguments[0];
                    if (listType.IsValueType)
                    {
                        // Swap
                        newField.SetValue(newInstance, oldValue);
                        oldField.SetValue(oldInstance, null);
                    }
                }
                else
                {
                    if (!ctx.GetNewReference(oldValue, out newValue))
                    {
                        newValue = ctx.Create(newField.FieldType);
                        AdaptInstance(ctx, oldValue, oldField.FieldType, newValue, newField.FieldType);
                    }

                    newField.SetValue(newInstance, newValue);
                }

            }
        }

        void AdaptInstance(TransitionContext ctx, object oldInstance, Type oldType, object newInstance, Type newType)
        {
            ctx.MapReference(oldInstance, newInstance);

            var fields = newType.GetRuntimeFields();
            foreach (var newField in fields)
            {
                Console.WriteLine("Runtime: {0}", newField.Name);

                var oldField = oldType.GetRuntimeFields().FirstOrDefault(a => a.Name == newField.Name);
                if (oldField != null)
                {
                    AdaptField(ctx, oldInstance, oldField, newInstance, newField);
                }

            }
        }
        void AdaptClasses(TransitionContext ctx, Assembly oldAssembly, Assembly newAssembly)
        {
            foreach (var newType in newAssembly.GetTypes())
            {
                if (!newType.IsClass)
                    continue;

                // TODO: Fix all statics.
            }
        }

        void UnloadPluginInstanceRecursive(Plugin plugin, object obj, HashSet<object> processed)
        {
            processed.Add(obj);

            var instType = obj.GetType();
            var funcs = instType.GetRuntimeMethods();

            foreach (var fn in funcs)
            {
                var attribs = fn.GetCustomAttributes();
                foreach (var attrib in attribs)
                {
                    if (attrib is Command cmd)
                    {
                        Commands.Remove(cmd.Name);
                    }
                }
            }

            var fields = instType.GetRuntimeFields();
            foreach (var field in fields)
            {
                var fieldType = field.FieldType;
                if (IsSystemType(field.FieldType))
                    continue;

                if (fieldType.IsClass && !fieldType.IsArray)
                {
                    var nextObj = field.GetValue(obj);
                    if (!processed.Contains(nextObj))
                        UnloadPluginInstanceRecursive(plugin, nextObj, processed);
                }
            }

            var props = instType.GetRuntimeProperties();
            foreach (var prop in props)
            {
                var fieldType = prop.PropertyType;
                if (IsSystemType(fieldType))
                    continue;
                if (prop.GetIndexParameters().Count() > 0)
                    continue;
                if (fieldType.IsClass && !fieldType.IsArray)
                {
                    var nextObj = prop.GetValue(obj);
                    if (!processed.Contains(nextObj))
                        UnloadPluginInstanceRecursive(plugin, nextObj, processed);
                }
            }
        }

        void UnloadPluginInstance(Plugin plugin)
        {
            if (plugin.Instance == null)
                return;

            UnloadPluginInstanceRecursive(plugin, plugin.Instance, new());
        }

        void LoadPluginInstanceRecursive(Plugin plugin, object obj, HashSet<object> processed)
        {
            processed.Add(obj);

            var instType = obj.GetType();
            var funcs = instType.GetRuntimeMethods();

            foreach (var fn in funcs)
            {
                var attribs = fn.GetCustomAttributes();
                foreach (var attrib in attribs)
                {
                    if (attrib is Command cmd)
                    {
                        Commands.Handler cb = null;

                        if (fn.ReturnType == typeof(void))
                        {
                            var cb2 = fn.CreateDelegate<Commands.HandlerVoid>(obj);
                            cb = (string[] args) =>
                            {
                                cb2(args);
                                return true;
                            };
                        }
                        else if (fn.ReturnType == typeof(bool))
                        {
                            cb = fn.CreateDelegate<Commands.Handler>(obj);
                        }
                        Commands.Register(cmd.Name, cmd.DebugOnly, cb);
                    }
                }
            }

            var fields = instType.GetRuntimeFields();
            foreach (var field in fields)
            {
                var fieldType = field.FieldType;
                if (IsSystemType(fieldType))
                    continue;

                if (fieldType.IsClass && !fieldType.IsArray)
                {
                    var nextObj = field.GetValue(obj);
                    if (!processed.Contains(nextObj))
                    {
                        LoadPluginInstanceRecursive(plugin, nextObj, processed);
                    }
                }
            }

            var props = instType.GetRuntimeProperties();
            foreach (var prop in props)
            {
                var fieldType = prop.PropertyType;
                if (IsSystemType(fieldType))
                    continue;
                if (prop.GetIndexParameters().Count() > 0)
                    continue;

                if (fieldType.IsClass && !fieldType.IsArray)
                {
                    var nextObj = prop.GetValue(obj);
                    if (!processed.Contains(nextObj))
                    {
                        LoadPluginInstanceRecursive(plugin, nextObj, processed);
                    }
                }
            }

        }

        void LoadPluginInstance(Plugin plugin)
        {
            if (plugin.Instance == null)
                return;

            LoadPluginInstanceRecursive(plugin, plugin.Instance, new());
        }

        void ReloadPlugin(Plugin plugin, string newAssemblyPath)
        {
            Console.WriteLine("Reloading '{0}'", plugin.Info.Name);

            UnloadPluginInstance(plugin);

            try
            {
                var loader = new LoaderContext();
                var newAssembly = loader.LoadFromFile(newAssemblyPath);

                var pluginClass = GetPluginClass(newAssembly);
                if (pluginClass != null)
                {
                    Console.WriteLine("Entry class: {0}", pluginClass.Name);
                }

                // NOTE: RemapContext stores old references, to fully unload the dll
                // it must be disposed first.
                using (var ctx = new TransitionContext())
                {
                    var newInstance = ctx.Create(pluginClass);
                    var hotReload = false;

                    if (plugin.Instance != null)
                    {
                        AdaptClasses(ctx, plugin.Loader.Current, newAssembly);
                        AdaptInstance(ctx, plugin.Instance, plugin.InstanceType, newInstance, pluginClass);

                        plugin.Instance = newInstance as IPlugin;
                        plugin.InstanceType = pluginClass;
                        hotReload = true;
                    }
                    else
                    {
                        // Initial startup.
                        var ctor = pluginClass.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Array.Empty<Type>(), null);
                        if (ctor != null)
                        {
                            ctor.Invoke(newInstance, Array.Empty<object>());
                        }


                        var startup = pluginClass.GetMethod("Startup");
                        if (startup != null)
                        {
                            try
                            {
                                startup.Invoke(newInstance, Array.Empty<object>());
                            }
                            catch (Exception ex)
                            {

                                throw;
                            }
                        }
                    }

                    plugin.Instance = newInstance as IPlugin;
                    plugin.InstanceType = pluginClass;

                    if (hotReload)
                    {
                        var reloadables = ctx.GetObjectsWithInterface(typeof(IHotload));
                        foreach (var obj in reloadables)
                        {
                            var reloadable = obj as IHotload;
                            reloadable.OnHotload();
                        }
                    }
                }

                if (plugin.Loader != null)
                {
                    var cur = plugin.Loader;
                    plugin.Loader = null;

                    var oldAssemblyPath = plugin.AssemblyPath;
                    var oldPdbPath = oldAssemblyPath.Replace(".dll", ".pdb");

                    cur.UnloadCurrent();
                    cur = null;

                    for (int i = 0; i < 50; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }

                    Task.Run(async delegate
                    {
                        await Task.Delay(2000);

                        // Remove previous assembly.
                        try
                        {
                            System.IO.File.Delete(oldAssemblyPath);
                        }
                        catch (Exception)
                        {
                            Console.WriteLine("WARNING: Unable to remove old assembly, ensure no references are stored.");
                        }

                        // Remove previous debug symbols.
                        // NOTE: If the debugger is attached this may be locked.
                        try
                        {
                            System.IO.File.Delete(oldPdbPath);
                        }
                        catch (Exception)
                        {
                            Console.WriteLine("WARNING: Unable to remove old PDB file, will be removed next start, probably locked by debugger.");
                        }
                    });
                }

                plugin.Loader = loader;
                plugin.AssemblyPath = newAssemblyPath;
            }
            catch (System.Exception ex)
            {
                Console.WriteLine("Exception: {0}", ex.ToString());
                return;
            }

            LoadPluginInstance(plugin);

            Console.WriteLine("Reloaded '{0}'", plugin.Info.Name);
        }

        IPlugin CreatePluginInstance(Plugin plugin)
        {
            return null;
        }

        void UnloadPlugin(Plugin plugin)
        {

        }


    }
}