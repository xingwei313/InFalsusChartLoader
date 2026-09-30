using System;
using System.Collections.Generic;
using System.Reflection;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Finds the native entry point of an IL2CPP method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One route: the Cpp2IL interop assembly keeps a hidden static field per method holding the
    /// runtime MethodInfo*, whose first field is the method pointer. That assembly is generated from
    /// the game that is installed, so a name follows the game across updates; an address does not.
    /// </para>
    /// <para>
    /// There is deliberately no fallback to a reversed RVA. There used to be, and it was not a safety
    /// net: when the name lookup failed — the only signal that the game has moved something — the
    /// stale RVA was patched anyway and the log said the hook was in place. The failure therefore read
    /// as success, the detour was never entered, and the counter that would have shown it sat at zero
    /// looking exactly like "the game never calls this". That cost two rounds on
    /// <c>AddressableHandleAutoReleaser._MIA</c> (`CHARTLOADER_HANDOFF_V6` §2–§4). A name that does
    /// not resolve is a fact the run has to hear.
    /// </para>
    /// <para>
    /// A name is not enough when the method is overloaded, because this takes the first method it
    /// finds with that name. The methods this mod resolves by name were each checked against the dump
    /// and are unique; if one ever gains an overload, the sibling mod's <c>BySignature</c> (name plus
    /// first parameter type) is the shape to bring over rather than a guess at which came first.
    /// </para>
    /// </remarks>
    internal static unsafe class MethodResolver
    {
        private const BindingFlags AnyMethod =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>
        /// A method's native address, by name, or zero — and a line in the log either way.
        /// </summary>
        internal static IntPtr ByName(string typeName, string methodName)
        {
            IntPtr p = ViaMethodInfo(typeName, methodName);
            if (p != IntPtr.Zero)
            {
                Diagnostics.Info($"{typeName}.{methodName} -> 0x{p.ToInt64():X} (MethodInfo)");
                return p;
            }

            Diagnostics.Error($"{typeName}.{methodName}: no method by that name in this build");
            return IntPtr.Zero;
        }

        /// <summary>
        /// The runtime's `MethodInfo` for a method, or zero.
        ///
        /// <see cref="ByName"/> answers with the code address, which is the first word of this
        /// structure. That is all a call site needs, but it is not enough to compare one resolved
        /// method against another, so this hands out the structure itself.
        /// </summary>
        internal static IntPtr MethodInfoOf(string typeName, string methodName)
        {
            try
            {
                if (!InteropTypeIndex.ByName().TryGetValue(typeName, out List<Type> types))
                    return IntPtr.Zero;

                foreach (Type t in types)
                {
                    foreach (MethodInfo mi in t.GetMethods(AnyMethod | BindingFlags.DeclaredOnly))
                    {
                        if (mi.Name != methodName) continue;

                        FieldInfo f = Il2CppInterop.Common.Il2CppInteropUtils
                            .GetIl2CppMethodInfoPointerFieldForGeneratedMethod(mi);
                        if (f == null) continue;

                        IntPtr info = (IntPtr)f.GetValue(null);
                        if (info != IntPtr.Zero) return info;
                    }
                }
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"MethodInfo lookup for {typeName}.{methodName} failed: " +
                                 Diagnostics.Describe(e));
            }
            return IntPtr.Zero;
        }

        private static IntPtr ViaMethodInfo(string typeName, string methodName)
        {
            IntPtr info = MethodInfoOf(typeName, methodName);
            if (info == IntPtr.Zero) return IntPtr.Zero;

            // The MethodInfo's first field is the compiled method pointer.
            return *(IntPtr*)info;
        }

        /// <summary>
        /// A method's native address, walked out of the runtime instead of read through reflection.
        ///
        /// <para>
        /// This is the only route that works for a <b>generic</b> method, and reflection is not merely
        /// unreliable there — it throws. The generated field for a method whose signature contains a
        /// type parameter has a type with `ContainsGenericParameters`, and `FieldInfo.GetValue` on one
        /// raises `InvalidOperationException: Late bound operations cannot be performed on fields with
        /// types for which Type.ContainsGenericParameters is true`. <see cref="MethodInfoOf"/> catches
        /// that and answers zero, and <see cref="ByName"/> reports the miss — there is no RVA to fall
        /// back to any more, and that is the point (see its remarks).
        /// </para>
        /// <para>
        /// That is exactly what happened to `AddressableHandleAutoReleaser._MIA`, and it is worth
        /// stating plainly because of how it failed: the miss was papered over with a reversed RVA, the
        /// hook was installed at whatever occupied that address, `Attach()` reported nothing wrong, and
        /// the detour was simply never entered — so the counter read zero for the rest of the run and
        /// looked identical to "the game never calls this".
        /// </para>
        /// <para>
        /// `il2cpp_class_get_methods` has no such limit: it reports the methods the class actually has,
        /// generic or not, and the first word of each `MethodInfo` is its code pointer. The class is
        /// reached the same two ways <see cref="Require"/>-style callers reach any other: the generated
        /// store first, then the runtime by image and namespace.
        /// </para>
        /// </summary>
        internal static IntPtr ByRuntime(string typeName, string methodName, string image, string namespaze)
        {
            IntPtr klass = ClassOf(typeName, image, namespaze);
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                // A class whose initialiser has not run has nothing to walk, the same as anywhere
                // else this mod reaches a class by name.
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(klass);

                IntPtr iter = IntPtr.Zero;
                IntPtr method;
                IntPtr found = IntPtr.Zero;

                // The walk's own variable, not the loop's: `il2cpp_class_get_methods` answers zero to
                // end the loop, so after it `method` holds zero and anything done with it is a null
                // dereference. It is kept here because it is needed again below, once the walk is over.
                IntPtr matched = IntPtr.Zero;

                while ((method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    // The `_` variant reads to a terminator; the other one trusts a length field it
                    // finds, which is how a walk becomes an OutOfMemoryException that names no call.
                    string name = Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name_(method);
                    if (name != methodName) continue;

                    IntPtr code = *(IntPtr*)method;

                    // Everything known about it, on the ground that "this method has no code of its
                    // own" and "the walk found nothing" are different answers and this is the only
                    // place the difference can be read. `_MIA<T>` is the first of those: `dump.cs`
                    // gives the definition `RVA: -1` and lists a single instantiation, `_MIA<object>`,
                    // which is the body every reference-type call goes to. Whether the runtime hands
                    // the definition a pointer to that body is not written down anywhere in the
                    // dumper's output -- so it is asked, not assumed.
                    Diagnostics.Info($"  {typeName}.{name}: code=0x{code.ToInt64():X} " +
                                     $"generic={Il2CppInterop.Runtime.IL2CPP.il2cpp_method_is_generic(method)} " +
                                     $"inflated={Il2CppInterop.Runtime.IL2CPP.il2cpp_method_is_inflated(method)} " +
                                     $"params={(int)Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_param_count(method)} " +
                                     $"words={JacketFactory.Words(method, 4)}");

                    if (code != IntPtr.Zero && found == IntPtr.Zero)
                    {
                        found = code;
                        matched = method;
                    }
                }

                if (found != IntPtr.Zero)
                {
                    // Before trusting it: for a shared generic the definition's own pointer is not
                    // the address the call sites use. Measured on `_MIA` — the walk resolved a real
                    // function, the hook installed there, and the detour was never entered while the
                    // game went on drawing shipped jackets. `dump.cs` names what the callers do use:
                    // the method's single instantiation. So if this is generic, ask for that.
                    if (Il2CppInterop.Runtime.IL2CPP.il2cpp_method_is_generic(matched) &&
                        Il2CppInterop.Runtime.IL2CPP.il2cpp_method_is_inflated(matched) == false)
                    {
                        IntPtr inflated = Inflated(klass, matched, typeName, methodName);
                        if (inflated != IntPtr.Zero && inflated != found)
                        {
                            Diagnostics.Info($"{typeName}.{methodName} -> 0x{inflated.ToInt64():X} " +
                                             "(the instantiation, not the definition)");
                            return inflated;
                        }
                    }

                    Diagnostics.Info($"{typeName}.{methodName} -> 0x{found.ToInt64():X} (runtime walk)");
                    return found;
                }

                Diagnostics.Warn($"the runtime walk of {typeName} found no {methodName} with a code " +
                                 "pointer of its own");
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"the runtime walk for {typeName}.{methodName} failed: {Diagnostics.Describe(e)}");
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// The code pointer of a generic method's compiled instantiation, or zero.
        ///
        /// A generics problem, not a version problem: `il2cpp_class_get_methods` reports the
        /// <b>definition</b>, and for a shared generic the definition's own pointer is not where the
        /// callers go. `dump.cs` shows the split plainly for `_MIA`: the definition is `// RVA: -1`
        /// — no code at all — and its `GenericInstMethod` block lists one instantiation,
        /// `_MIA&lt;object&gt;`, which is the body every reference-type call reaches.
        ///
        /// The runtime can inflate a definition, but only through its own reflection: the native
        /// `MethodInfo` is turned into a `System.Reflection.MethodInfo`, `MakeGenericMethod` is
        /// invoked on it <b>through the runtime</b>, and the result is turned back into a native
        /// `MethodInfo`. Every call is the game's own API, so this is not pinned to a build. There is
        /// no direct "inflate this" entry point in the interop's `il2cpp_*` surface — the round trip
        /// is the only route, which is why it reads the way it does.
        ///
        /// `object` is the type argument because reference-type instantiations share one body and
        /// `object` is the name the dumper gave it; the root of the class hierarchy is therefore
        /// also the right thing to ask for.
        /// </summary>
        private static IntPtr Inflated(IntPtr klass, IntPtr method, string typeName, string methodName)
        {
            try
            {
                // `System.Object` — walk up from a class that certainly exists.
                IntPtr objectClass = klass;
                for (IntPtr parent = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_parent(objectClass);
                     parent != IntPtr.Zero; )
                {
                    objectClass = parent;
                    parent = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_parent(objectClass);
                }

                IntPtr objectType = Il2CppInterop.Runtime.IL2CPP.il2cpp_type_get_object(
                    Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_type(objectClass));
                if (objectType == IntPtr.Zero)
                {
                    Diagnostics.Warn($"could not get System.Object as a Type for {typeName}.{methodName}");
                    return IntPtr.Zero;
                }

                // MakeGenericMethod takes a Type[], so one has to be built. The element class is the
                // class of the Type object just obtained, which is what an array of Type wants.
                IntPtr typeArrayClass = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_class_get(
                    Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(objectType), 1);
                if (typeArrayClass == IntPtr.Zero) return IntPtr.Zero;

                IntPtr arguments = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_new(typeArrayClass, 1);
                if (arguments == IntPtr.Zero) return IntPtr.Zero;
                *(IntPtr*)(arguments + Offsets.Runtime.ArrayDataOffset) = objectType;

                IntPtr reflection = Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_object(method, klass);
                if (reflection == IntPtr.Zero)
                {
                    Diagnostics.Warn($"{typeName}.{methodName} could not be turned into a reflection method");
                    return IntPtr.Zero;
                }

                IntPtr reflectionClass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(reflection);
                IntPtr make = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(
                    reflectionClass, "MakeGenericMethod", 1);
                if (make == IntPtr.Zero)
                {
                    Diagnostics.Warn("the reflection method has no MakeGenericMethod");
                    return IntPtr.Zero;
                }

                IntPtr* args = stackalloc IntPtr[1];
                args[0] = arguments;

                IntPtr raised = IntPtr.Zero;
                IntPtr inflated = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(
                    make, reflection, (void**)args, ref raised);
                if (raised != IntPtr.Zero)
                {
                    Diagnostics.Warn($"MakeGenericMethod raised for {typeName}.{methodName}: {Raised.Text(raised)}");
                    return IntPtr.Zero;
                }
                if (inflated == IntPtr.Zero) return IntPtr.Zero;

                IntPtr native = Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_from_reflection(inflated);
                if (native == IntPtr.Zero)
                {
                    Diagnostics.Warn($"the inflated {typeName}.{methodName} could not be turned back " +
                                     "into a native method");
                    return IntPtr.Zero;
                }

                IntPtr code = *(IntPtr*)native;
                Diagnostics.Info($"  {typeName}.{methodName}<object>: code=0x{code.ToInt64():X} " +
                                 $"words={JacketFactory.Words(native, 4)}");
                return code;
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"inflating {typeName}.{methodName} failed: {Diagnostics.Describe(e)}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// A method's <c>MethodInfo*</c>, walked out of the runtime.
        ///
        /// <see cref="ByRuntime"/> answers with the <b>code pointer</b> — the first word of the
        /// structure — because that is what a detour needs. A caller that means to <i>invoke</i> the
        /// method needs the structure itself: `il2cpp_runtime_invoke` dereferences its argument as a
        /// `MethodInfo`, so handing it a code pointer makes the runtime read machine code as a
        /// structure. That is an access violation, and it is what happened the first time this was
        /// written — the two are one word apart and the compiler cannot tell them apart.
        /// </summary>
        internal static IntPtr MethodInfoByRuntime(string typeName, string methodName, string image, string namespaze)
        {
            IntPtr klass = ClassOf(typeName, image, namespaze);
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(klass);

                IntPtr iter = IntPtr.Zero;
                IntPtr method;
                while ((method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    if (Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name_(method) != methodName) continue;

                    Diagnostics.Info($"{typeName}.{methodName} -> MethodInfo at 0x{method.ToInt64():X} (runtime walk)");
                    return method;
                }

                Diagnostics.Warn($"the runtime walk of {typeName} found no method named {methodName}");
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"the runtime walk for {typeName}.{methodName} failed: {Diagnostics.Describe(e)}");
            }

            return IntPtr.Zero;
        }

        /// <summary>A class pointer: the generated store first, then the runtime by image and name.</summary>
        private static IntPtr ClassOf(string typeName, string image, string namespaze)
        {
            IntPtr klass = FieldResolver.ClassPointer(typeName);
            if (klass != IntPtr.Zero) return klass;

            // The store is filled by the generated type's own initialiser, and a type nothing has
            // touched yet may not have run one. Asking the runtime does not depend on that.
            klass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(image, namespaze, typeName);
            if (klass != IntPtr.Zero) return klass;

            Diagnostics.Warn($"{typeName} could not be found, by generated store or by image '{image}'");
            return IntPtr.Zero;
        }
    }
}
