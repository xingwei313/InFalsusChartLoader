using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Turns a PNG on disk into a `Material` the game can draw a jacket with.
    ///
    /// <para>
    /// Everything here goes through the runtime rather than through the generated interop types.
    /// Those types are convenient but this repository has already recorded that `Il2CppType.Of&lt;T&gt;()`
    /// does not reliably produce a class pointer for generated assemblies, and a jacket is not worth
    /// betting on it: reaching for `il2cpp_object_new` and the method table is more code and has one
    /// failure mode instead of two. The generated assembly is still read, but only for the one thing
    /// it is the only source of — a method's full signature, which is what tells nineteen
    /// `Texture2D` constructors apart.
    /// </para>
    /// <para>
    /// The two calls that can silently produce nothing are each followed by a check of the object
    /// they were supposed to fill in, because a constructor that returns is not evidence that it
    /// built anything and the difference does not surface until something else touches the object.
    /// A check that cannot be made is not a failure: it is reported once, and the next piece of
    /// evidence decides.
    /// </para>
    /// <para>
    /// The shader is chosen by name and is allowed to be wrong. The only consumer of this Material
    /// copies its `mainTexture` into a template the game already owns and discards the rest, so what
    /// this has to get right is the pixels.
    /// </para>
    /// </summary>
    internal static unsafe class JacketFactory
    {
        /// <summary>
        /// The texture formats to try, as `TextureFormat` values, in order of how well they carry
        /// four-channel pixels.
        ///
        /// The first is the one this wants — `RGBA32` is four bytes in the order the decoder already
        /// produces, so the array needs no conversion. It is not asked for blindly, because the
        /// constructor refuses a format the device does not support, and that refusal arrives as
        /// `ArgumentException: Failed SupportsTextureFormat; format is not a valid TextureFormat`
        /// from inside Unity — a message about the format, not about this mod. `SystemInfo` answers
        /// the question properly, so the format is picked rather than assumed.
        /// </summary>
        private static readonly int[] TextureFormats = { 4 /* RGBA32 */, 5 /* ARGB32 */, 3 /* RGB24 */ };

        /// <summary>`Color32` is four bytes — red, green, blue, alpha — so decoded pixels fit it as-is.</summary>
        private const int Color32Size = 4;

        /// <summary>
        /// Shaders to try, in order. A name that resolves in the editor can be stripped from a build,
        /// so the first is a preference and not an assumption — and if none resolves the jacket is
        /// built anyway, because nothing ever reads this material's shader.
        /// </summary>
        private static readonly string[] ShaderNames = { "Unlit/Texture", "Sprites/Default", "UI/Default" };

        private static IntPtr _textureClass;
        private static IntPtr _materialClass;
        private static IntPtr _shaderClass;
        private static IntPtr _color32Class;

        private static bool _resolved;

        /// <summary>
        /// Finds the four types a jacket is built from — and only those four.
        ///
        /// Each is asked for by name and reported by name when it is missing. An earlier version
        /// required a fifth, `String`, and used it nowhere: a type this file never touches was able
        /// to veto every jacket, and the only thing it said about it was that "the types" were not
        /// all found. A dependency that is not used is a dependency that can only fail.
        /// </summary>
        private static bool Resolve()
        {
            if (_resolved) return true;

            _textureClass = Require("Texture2D");
            _materialClass = Require("Material");
            _shaderClass = Require("Shader");
            _color32Class = Require("Color32");

            _resolved = _textureClass != IntPtr.Zero && _materialClass != IntPtr.Zero &&
                        _shaderClass != IntPtr.Zero && _color32Class != IntPtr.Zero;

#if DEBUG
            // What the running game actually exposes for these types, which is not what Cecil reads
            // off the same file: the generated `NativeMethodInfoPtr_*` fields are visible to Cecil
            // and were not visible to reflection.
            JacketProbe.Report();
#endif

            return _resolved;
        }

        /// <summary>Where the types below live. The value types live somewhere else again.</summary>
        private const string Assembly = "UnityEngine.CoreModule.dll";
        private const string Namespace = "UnityEngine";

        /// <summary>
        /// The same assembly under the name reflection knows it by, which is not the file name.
        ///
        /// `GetIl2CppClass` wants the module the game's metadata records —
        /// `UnityEngine.CoreModule.dll` — while an `Assembly` in the CLR is named
        /// `UnityEngine.CoreModule` with no extension. Passing the file name to the second gives no
        /// match and no exception, only an assembly that all exists and cannot be found.
        /// </summary>
        private const string AssemblyName = "UnityEngine.CoreModule";

        /// <summary>
        /// The constructor wanted, as its parameter type names in order —
        /// `Texture2D(int, int, TextureFormat, bool, bool)`.
        ///
        /// Matched on this rather than on the generated field's name because the name is a string
        /// this file would be guessing at, while the parameter list is what the runtime itself
        /// reports. The two boolean parameters are what separate it from
        /// `(int, int, TextureFormat, int, bool)`, which has the same length and the same name.
        /// </summary>
        private static readonly string[] ConstructorWanted =
            { "Int32", "Int32", "TextureFormat", "Boolean", "Boolean" };

        private static IntPtr Require(string typeName) => Require(typeName, Assembly, Namespace);

        private static IntPtr Require(string typeName, string assembly, string namespaze)
        {
            IntPtr klass = FieldResolver.ClassPointer(typeName);
            if (klass != IntPtr.Zero) return klass;

            // The generated store is filled by the generated type's own initialiser, and a type
            // nothing has touched yet may not have run one. Asking the runtime directly does not
            // depend on that, so it is tried before giving up.
            klass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(assembly, namespaze, typeName);
            if (klass != IntPtr.Zero)
            {
                // The generated types do this in their own initialiser, right after they resolve the
                // class and before they look anything up on it. A class reached this way has not had
                // it done, and querying an uninitialised class is not safe.
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(klass);

                Diagnostics.Info($"{typeName} found by assembly, not by the generated store");
                return klass;
            }

            Diagnostics.Warn($"the game's {typeName} type could not be found, by store or by assembly");
            return IntPtr.Zero;
        }

        /// <summary>
        /// Builds a Material showing <paramref name="pngPath"/>. Zero, with a reason, when the file
        /// is not a PNG this mod can decode or the game's own types cannot be reached.
        /// </summary>
        internal static IntPtr Build(string pngPath, out string reason)
        {
            reason = null;

            if (!Resolve())
            {
#if DEBUG
                reason = "the game's texture and material types could not be found";
#endif
                return IntPtr.Zero;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(pngPath);
            }
            catch (Exception e)
            {
                // On Release this catch body is empty, because the message below is Debug-only and
                // it is the only thing that reads `e`. The parameter stays named rather than being
                // dropped: a named one that a `#if` block uses compiles on both sides, where an
                // anonymous one would have to be paired with a separately named inner catch.
#if DEBUG
                reason = $"{Path.GetFileName(pngPath)} could not be read: {Diagnostics.Describe(e)}";
#else
                _ = e;
#endif
                return IntPtr.Zero;
            }

            Png.Image image = Png.Decode(bytes, out reason);
            if (image == null) return IntPtr.Zero;

            IntPtr texture = BuildTexture(image, out reason);
            if (texture == IntPtr.Zero) return IntPtr.Zero;

            IntPtr material = BuildMaterial(texture, out reason);
            if (material == IntPtr.Zero) return IntPtr.Zero;

            Diagnostics.Info($"jacket '{Path.GetFileName(pngPath)}': {image.Width}x{image.Height}");
            return material;
        }

        /// <summary>
        /// A `Texture2D` holding the decoded pixels.
        ///
        /// The elements of a `Color32[]` are four bytes in exactly the order the decoder produces, so
        /// the copy is the whole conversion -- but only after the rows are turned around. Unity's
        /// `SetPixels32` is documented as taking them bottom to top and a PNG stores them top to
        /// bottom, so the two conventions differ on one axis and the copy has to say which it means.
        /// </summary>
        private static IntPtr BuildTexture(Png.Image image, out string reason)
        {
            reason = null;

            int pixels = image.Width * image.Height;

            // One line per native call. A call that raises leaves no stack to read -- the runtime
            // surfaces it as an SEH exception at whatever frame happened to be current -- so the
            // only way to learn which one it was is to have written down the last one that worked.
            Diagnostics.Info("jacket step: allocating the Color32 array");
            IntPtr colors = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_new(_color32Class, (ulong)pixels);
            if (colors == IntPtr.Zero)
            {
#if DEBUG
                reason = "the jacket's pixel array could not be allocated";
#endif
                return IntPtr.Zero;
            }

            // Row by row, last row first. Unity's `SetPixels32` takes the colours left to right and
            // **bottom to top**, while a PNG stores its rows top to bottom -- and the decoder here
            // produces them in PNG order, verified byte for byte against PIL. So a straight copy
            // builds a correctly-sized picture standing on its head, which is exactly what it did.
            // The columns are left alone: Unity's horizontal order already matches PNG's.
            byte* destination = (byte*)(colors + Offsets.Runtime.ArrayDataOffset);
            long rowBytes = (long)image.Width * Color32Size;

            fixed (byte* source = image.AsColor32)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    Buffer.MemoryCopy(source + (image.Height - 1 - y) * rowBytes,
                                      destination + (long)y * rowBytes,
                                      rowBytes, rowBytes);
                }
            }

            Diagnostics.Info("jacket step: creating the texture object");
            IntPtr texture = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(_textureClass);
            if (texture == IntPtr.Zero)
            {
#if DEBUG
                reason = "the jacket's texture could not be created";
#endif
                return IntPtr.Zero;
            }

            // Texture2D(int width, int height, TextureFormat format, bool mipChain, bool linear) --
            // by signature, because five constructors take five arguments and one of the others is
            // (Int32, Int32, TextureFormat, Int32, Boolean): same name, same count, and an int where
            // this one has the first bool.
            Diagnostics.Info("jacket step: looking up Texture2D..ctor(int,int,TextureFormat,bool,bool)");
            IntPtr constructor = ConstructorBySignature("Texture2D", ConstructorWanted);
            if (constructor == IntPtr.Zero)
            {
#if DEBUG
                reason = "Texture2D's constructor could not be found";
#endif
                return IntPtr.Zero;
            }

            int format = SupportedFormat();
            if (format == 0)
            {
#if DEBUG
                reason = "the device supports none of the texture formats a jacket can use";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info($"jacket step: calling Texture2D..ctor with format {format}");

            // Five value-type arguments, so five addresses — see Invoke. The bytes have to stay put
            // for the length of the call, which is what locals in this frame are for.
            int width = image.Width;
            int height = image.Height;
            int textureFormat = format;
            byte mipChain = 0;
            byte linear = 0;

            IntPtr* ctorArgs = stackalloc IntPtr[5];
            ctorArgs[0] = (IntPtr)(&width);
            ctorArgs[1] = (IntPtr)(&height);
            ctorArgs[2] = (IntPtr)(&textureFormat);
            ctorArgs[3] = (IntPtr)(&mipChain);
            ctorArgs[4] = (IntPtr)(&linear);
            if (!Invoke(constructor, texture, ctorArgs, 5, out reason)) return IntPtr.Zero;

            // A constructor that returns is not a texture that exists. Two things separate the two
            // without trusting either: the engine's own pointer behind the object, and the object's
            // answer when asked how wide it is.
            if (IsKnownToHaveNoNativeObject(texture))
            {
#if DEBUG
                reason = "Texture2D's constructor returned but the texture has no native object";
#endif
                return IntPtr.Zero;
            }

            int built = WidthOf(texture);
            if (built != image.Width)
            {
#if DEBUG
                reason = $"Texture2D's constructor returned but the texture reports {built} pixels " +
                         $"wide instead of {image.Width}";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info("jacket step: looking up SetPixels32(Color32[])");
            IntPtr setPixels = Entry(_textureClass, "SetPixels32", 1);
            if (setPixels == IntPtr.Zero)
            {
#if DEBUG
                reason = "Texture2D.SetPixels32 could not be found";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info("jacket step: calling SetPixels32");
            IntPtr* pixelsArgs = stackalloc IntPtr[1];
            pixelsArgs[0] = colors;
            if (!Invoke(setPixels, texture, pixelsArgs, 1, out reason)) return IntPtr.Zero;

            Diagnostics.Info("jacket step: looking up Apply()");
            IntPtr apply = Entry(_textureClass, "Apply", 0);
            if (apply == IntPtr.Zero)
            {
#if DEBUG
                reason = "Texture2D.Apply could not be found";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info("jacket step: calling Apply");
            if (!Invoke(apply, texture, null, 0, out reason)) return IntPtr.Zero;

            return texture;
        }

        /// <summary>
        /// A `Material` whose main texture is <paramref name="texture"/>.
        ///
        /// Only the texture is ever read from it, so a shader that fails to resolve is not fatal —
        /// it is reported, and the jacket is built anyway, because the alternative is no jacket at
        /// all over a property nothing looks at.
        /// </summary>
        private static IntPtr BuildMaterial(IntPtr texture, out string reason)
        {
            reason = null;

            IntPtr shader = IntPtr.Zero;
            IntPtr find = Entry(_shaderClass, "Find", 1);
            IntPtr* findArgs = stackalloc IntPtr[1];

            if (find != IntPtr.Zero)
            {
                // Tried in turn because a name that resolves in the editor can be stripped from a
                // build; the last is the one Unity guarantees for UI.
                foreach (string name in ShaderNames)
                {
                    findArgs[0] = Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(name);
                    if (findArgs[0] == IntPtr.Zero) continue;

                    IntPtr raised = IntPtr.Zero;
                    IntPtr found = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(
                        find, IntPtr.Zero, (void**)findArgs, ref raised);

                    if (raised == IntPtr.Zero && found != IntPtr.Zero && Memory.LooksLikeObject(found))
                    {
                        shader = found;
                        break;
                    }
                }
            }

            if (shader == IntPtr.Zero) Diagnostics.Warn("no shader could be found for the jacket material");

            Diagnostics.Info("jacket step: creating the material object");
            IntPtr material = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(_materialClass);
            if (material == IntPtr.Zero)
            {
#if DEBUG
                reason = "the jacket's material could not be created";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info("jacket step: looking up Material..ctor(Shader)");
            IntPtr constructor = Entry(_materialClass, ".ctor", 1);
            if (constructor == IntPtr.Zero)
            {
#if DEBUG
                reason = "Material's constructor could not be found";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info("jacket step: calling Material..ctor");
            IntPtr* ctorArgs = stackalloc IntPtr[1];
            ctorArgs[0] = shader;
            if (!Invoke(constructor, material, ctorArgs, 1, out reason)) return IntPtr.Zero;

            // Same check as the texture's, for the same reason: a material is a managed shell until
            // a native one exists behind it. Unlike the texture there is no second question to ask
            // it here, so an unreadable `m_CachedPtr` leaves this unchecked rather than failed.
            if (IsKnownToHaveNoNativeObject(material))
            {
#if DEBUG
                reason = "Material's constructor returned but the material has no native object";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info("jacket step: looking up set_mainTexture(Texture)");
            IntPtr setTexture = Entry(_materialClass, "set_mainTexture", 1);
            if (setTexture == IntPtr.Zero)
            {
#if DEBUG
                reason = "Material.set_mainTexture could not be found";
#endif
                return IntPtr.Zero;
            }

            Diagnostics.Info("jacket step: calling set_mainTexture");
            IntPtr* textureArgs = stackalloc IntPtr[1];
            textureArgs[0] = texture;
            if (!Invoke(setTexture, material, textureArgs, 1, out reason)) return IntPtr.Zero;

            return material;
        }

        // ---------------------------------------------------------------- runtime plumbing

        /// <summary>
        /// The native entry point of a method, or zero.
        ///
        /// The entry point is the first field of the `MethodInfo`, which is what the sibling mod's
        /// resolver already relies on.
        ///
        /// This matches on name and parameter count alone, so it is only safe for a method with one
        /// overload. `Material..ctor` and the `SetPixels32`/`Apply`/`get_width` calls all pass that
        /// test; the texture constructors do not, and the one this file wanted was one of five with
        /// the same arity. Use <see cref="ConstructorBySignature"/> for those.
        /// </summary>
        private static IntPtr Entry(IntPtr klass, string name, int parameters) =>
            Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(klass, name, parameters);

        /// <summary>
        /// A constructor's `MethodInfo`, picked by its whole parameter list rather than by its arity.
        ///
        /// <para>
        /// `Texture2D` has nineteen constructors and five of them take five arguments. Two of those
        /// five are `(Int32, Int32, TextureFormat, Int32, Boolean)` and
        /// `(Int32, Int32, TextureFormat, Boolean, Boolean)` — same name, same count, and the third
        /// and fourth parameters mean different things. `il2cpp_class_get_method_from_name` cannot
        /// tell them apart, and it does not fail when it picks wrong: it hands back a real
        /// constructor, the call returns normally, and the object is left without a texture. The
        /// next call on it is what raises, which is a long way from the mistake.
        /// </para>
        /// <para>
        /// Enumerated through the game's own API rather than through reflection, because reflection
        /// does not see this type's methods at all. `UnityEngine.CoreModule` is one of the
        /// assemblies the interop layer substitutes, and for those the CLR reports every field and
        /// no method — the generated assembly on disk declares 187 methods on `Texture2D` while
        /// `Type.GetMethods` answers with none, and `GetField` fails by name on a field that is
        /// plainly there. Fields survive that substitution; methods do not.
        /// </para>
        /// <para>
        /// Every candidate is logged, so a miss says what was there instead of only that nothing
        /// matched.
        /// </para>
        /// </summary>
        private static IntPtr ConstructorBySignature(string typeName, string[] wanted)
        {
            if (Constructors.TryGetValue(typeName, out IntPtr known)) return known;

            IntPtr found = FindConstructorBySignature(typeName, wanted);
            Constructors[typeName] = found;
            return found;
        }

        /// <summary>
        /// Constructors already looked for, by type name. Zero means the search ran and found
        /// nothing, and that is remembered too.
        ///
        /// Keyed by the type alone because the wanted parameter list is a constant of this file, one
        /// per type. Worth caching for two reasons: the walk reads every method the class has, and it
        /// logs the first <see cref="MethodsToLog"/> of them by name — so a song with four pictures
        /// walked `Texture2D` four times and printed four identical thirty-line listings, which was
        /// most of a debug log's length for one song. The answer cannot change between jackets.
        /// </summary>
        private static readonly Dictionary<string, IntPtr> Constructors =
            new Dictionary<string, IntPtr>(StringComparer.Ordinal);

        private static IntPtr FindConstructorBySignature(string typeName, string[] wanted)
        {
            try
            {
                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(Assembly, Namespace, typeName);
                if (klass == IntPtr.Zero)
                {
                    Diagnostics.Warn($"{typeName}'s class could not be reached for its constructors");
                    return IntPtr.Zero;
                }

                // The generated code does this the moment it resolves a class, and `Require` does it
                // for the three types it reaches the same way. A class reached straight from the
                // runtime has had nothing run for it, and its methods are not safe to call until
                // something has.
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(klass);

                // Capped, and counted: an enumeration that never ends allocates its way to an
                // OutOfMemoryException that says nothing about which call was at fault. A class has
                // one method per declaration, so the cap only ever fires on a broken walk.
                IntPtr iter = IntPtr.Zero;
                IntPtr method;
                int walked = 0;
                while ((method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    walked++;
                    if (walked > MethodsWalkLimit)
                    {
                        Diagnostics.Warn($"{typeName}'s method walk passed {MethodsWalkLimit} entries; stopping");
                        return IntPtr.Zero;
                    }

                    string name = Name(method);
                    if (walked <= MethodsToLog) Diagnostics.Info($"  {typeName} method {walked}: '{(name ?? "(null)")}'");

                    if (name != ".ctor") continue;

                    int count = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_param_count(method);
                    if (count != wanted.Length) continue;

                    if (!Matches(method, wanted)) continue;

                    // The pointer came back from a walk, so before it is handed to the runtime it is
                    // asked who it belongs to. A MethodInfo that cannot name its own class is not one
                    // that can be invoked, and the invoke would fail with nothing to say — which is
                    // what an unnamed exception turned out to mean.
                    string owner = DeclaringTypeName(method);
                    if (owner != typeName)
                    {
                        Diagnostics.Warn($"a {typeName} constructor was found but reports {owner ?? "(null)"} " +
                                         $"as its class; it is not one that can be called");
                        return IntPtr.Zero;
                    }

                    Diagnostics.Info($"{typeName}({string.Join(", ", wanted)}) found among the class's methods");

#if DEBUG
                    // The bytes at that pointer, so that "the invoke raised nothing" stops being the
                    // only thing known about it. A MethodInfo is a run of pointers whose first is the
                    // code; one that does not look like that is not one the runtime can call, and
                    // that is visible here rather than only as an unnamed exception later.
                    Diagnostics.Info($"  the constructor's MethodInfo is at 0x{method.ToInt64():X}");
                    Diagnostics.Info($"  class=0x{klass.ToInt64():X}  code={Words(method, 12)}");
#endif

                    return method;
                }

                Diagnostics.Info($"{typeName}: walked {walked} method(s)");

#if DEBUG
                // Every constructor the class has, so that a miss says what it was choosing between.
                // The whole block is Debug-only rather than just its logging calls: a string built
                // inside a `[Conditional]` argument is still a string in the assembly, which is the
                // shape the artifact check exists to catch.
                int seen = 0;
                iter = IntPtr.Zero;
                while ((method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    if (Name(method) != ".ctor") continue;

                    var parameters = new System.Collections.Generic.List<string>();
                    int arity = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_param_count(method);
                    for (int i = 0; i < arity; i++)
                    {
                        parameters.Add(TypeName(Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_param(method, (uint)i)));
                    }

                    Diagnostics.Warn($"  {typeName}.ctor({string.Join(", ", parameters)})");
                    seen++;
                }
                Diagnostics.Warn($"the class offers {seen} constructor(s), none of them " +
                                 $"({string.Join(", ", ConstructorWanted)})");
#endif

                return IntPtr.Zero;
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"{typeName}'s constructors could not be read: {Diagnostics.Describe(e)}");
                return IntPtr.Zero;
            }
        }

        /// <summary>Whether a method's parameters are the wanted type names, in order.</summary>
        private static bool Matches(IntPtr method, string[] wanted)
        {
            for (int i = 0; i < wanted.Length; i++)
            {
                IntPtr type = Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_param(method, (uint)i);
                if (type == IntPtr.Zero) return false;

                if (TypeName(type) != wanted[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// A method's name, or null.
        ///
        /// The interop assembly offers each of these twice: once returning the raw pointer, and once
        /// returning the string. The second is the one to take. It reads to a terminator with
        /// `Marshal.PtrToStringUTF8`, where converting a raw pointer goes through
        /// `Il2CppStringToManaged`, which believes the length field it finds — and a pointer that is
        /// not a string yields a length that is not a length, which is how a walk ends in an
        /// OutOfMemoryException that names no call.
        /// </summary>
        private static string Name(IntPtr method) =>
            Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name_(method);

        /// <summary>
        /// The first of <see cref="TextureFormats"/> this device can actually hold, or zero when it
        /// can hold none. Zero is not a `TextureFormat`, so it cannot be mistaken for a result.
        ///
        /// Asked once and remembered: the answer is a property of the machine and does not change
        /// while the game runs.
        /// </summary>
        private static int SupportedFormat()
        {
            if (_format != int.MinValue) return _format;

            _format = 0;
            foreach (int candidate in TextureFormats)
            {
                if (!SupportsFormat(candidate)) continue;
                _format = candidate;
                Diagnostics.Info($"the device supports texture format {candidate}");
                break;
            }

            if (_format == 0)
                Diagnostics.Warn("the device reports no support for any texture format a jacket can use");
            return _format;
        }

        private static int _format = int.MinValue;

        /// <summary>
        /// Whether the device supports a `TextureFormat`, asked of `SystemInfo`.
        ///
        /// Every way of failing to ask is reported separately, because the caller turns a false into
        /// a message about the device — and "this machine cannot hold RGBA32" and "the question could
        /// not be put to it" are different facts that have been read as the same one. Each of these
        /// used to be a bare `return false`.
        /// </summary>
        private static bool SupportsFormat(int format)
        {
            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(Assembly, Namespace, "SystemInfo");
            if (klass == IntPtr.Zero)
            {
                Diagnostics.Warn("SystemInfo could not be reached, so the texture format was never asked about");
                return false;
            }

            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(klass);

            IntPtr method = Entry(klass, "SupportsTextureFormat", 1);
            if (method == IntPtr.Zero)
            {
                Diagnostics.Warn("SystemInfo.SupportsTextureFormat could not be found, so the texture " +
                                 "format was never asked about");
                return false;
            }

            IntPtr* args = stackalloc IntPtr[1];
            args[0] = (IntPtr)(&format);

            IntPtr raised = IntPtr.Zero;
            IntPtr boxed = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, (void**)args, ref raised);
            if (raised != IntPtr.Zero)
            {
                Diagnostics.Warn($"asking about texture format {format} raised: {Raised.Text(raised)}");
                return false;
            }
            if (boxed == IntPtr.Zero)
            {
                Diagnostics.Warn($"asking about texture format {format} answered with nothing");
                return false;
            }

            return *(byte*)(boxed + Offsets.Runtime.BoxedData) != 0;
        }

        /// <summary>
        /// The first words at an address, as hex, for comparing one structure with another.
        ///
        /// Debug-only, and deliberately raw: the point is to see what the runtime actually put
        /// there, not what any header in this repository says should be there.
        /// </summary>
        internal static string Words(IntPtr address, int count)
        {
#if DEBUG
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                IntPtr at = address + i * IntPtr.Size;
                if (i > 0) text.Append(' ');
                text.Append(Memory.Ptr(at).ToInt64().ToString("X"));
            }
            return text.ToString();
#else
            return null;
#endif
        }

        /// <summary>The class a method says it belongs to, or null when it cannot say.</summary>
        private static string DeclaringTypeName(IntPtr method)
        {
            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_declaring_type(method);
            if (klass == IntPtr.Zero) return null;
            return Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(klass);
        }

        /// <summary>
        /// A type's name, without the namespace and without the generic arguments — which is how the
        /// wanted parameter list is written.
        /// </summary>
        private static string TypeName(IntPtr type)
        {
            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_type(type);
            if (klass == IntPtr.Zero) return null;
            return Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(klass);
        }

        /// <summary>
        /// The generated type for a name, from the assembly the game's own code loads it from.
        ///
        /// <see cref="InteropTypeIndex"/> is deliberately built from the `Il2Cpp*` assemblies only,
        /// so the Unity types are not in it — but the generated `UnityEngine.CoreModule` assembly
        /// still exists and still has the per-method fields. It is found among the loaded assemblies
        /// rather than resolved by name, which would need the CLR to locate the file itself.
        ///
        /// A miss here used to be reported as "the type has no generated type", which was true of
        /// the search and not of the world: the name being compared was the module's file name, so
        /// no assembly could ever match. It now says which name it looked for.
        /// </summary>
        private static Type GeneratedType(string typeName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != AssemblyName) continue;
                return asm.GetType(Namespace + "." + typeName, throwOnError: false);
            }

            Diagnostics.Warn($"no loaded assembly is named '{AssemblyName}', so {typeName} cannot be reached");
            return null;
        }

        /// <summary>
        /// Whether an object is known to have no native object behind it — false when it has one, and
        /// false when that cannot be told.
        ///
        /// Unity keeps the pointer in `m_CachedPtr` on the base `Object`, and it stays zero until a
        /// native object exists. Reading it is a second opinion, not the verdict: the field is
        /// inherited and asked for by name, so a build where the name does not resolve would make
        /// every jacket fail on a check that answers nothing. When it cannot be read the answer is
        /// "not known", and the caller's own evidence decides.
        /// </summary>
        private static bool IsKnownToHaveNoNativeObject(IntPtr instance)
        {
            if (_cachedPtrOffset == int.MinValue) _cachedPtrOffset = CachedPointerOffset();

            if (_cachedPtrOffset <= 0) return false;

            return Memory.Ptr(instance + _cachedPtrOffset) == IntPtr.Zero;
        }

        /// <summary>
        /// Where `UnityEngine.Object.m_CachedPtr` sits, or zero when it cannot be reached — and the
        /// zero is what makes this asked once rather than once per jacket.
        ///
        /// <para>
        /// Reached by assembly and name, which is the whole of the fix. It used to go through
        /// <see cref="FieldResolver"/>, and that cannot work for a Unity type: the index behind it is
        /// built from the `Il2Cpp*` assemblies only (see <see cref="InteropTypeIndex"/>), so a lookup
        /// for `Object` was not a slow answer, it was a permanent miss. It therefore failed on every
        /// jacket, every time, and the warning said so — while the comment beside it claimed the
        /// failure was remembered. Two lines disagreeing, and the code was the wrong one.
        /// </para>
        /// <para>
        /// The field is on the base `Object`, which every built object's class descends from, so the
        /// offset is the same on a `Texture2D` and on a `Material`.
        /// </para>
        /// </summary>
        private static int CachedPointerOffset()
        {
            try
            {
                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(Assembly, Namespace, "Object");
                if (klass == IntPtr.Zero)
                {
                    Diagnostics.Warn("UnityEngine.Object could not be reached, so whether a built object " +
                                     "has a native one will not be checked");
                    return 0;
                }

                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(klass);

                IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "m_CachedPtr");
                if (field == IntPtr.Zero)
                {
                    Diagnostics.Warn("UnityEngine.Object has no m_CachedPtr, so whether a built object " +
                                     "has a native one will not be checked");
                    return 0;
                }

                int offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
                if (offset <= 0)
                {
                    Diagnostics.Warn($"m_CachedPtr is at 0x{offset:X}, which cannot be an offset; whether a " +
                                     "built object has a native one will not be checked");
                    return 0;
                }

                Diagnostics.Info($"m_CachedPtr is at 0x{offset:X}");
                return offset;
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"Object.m_CachedPtr could not be resolved: {Diagnostics.Describe(e)}");
                return 0;
            }
        }

        private static int _cachedPtrOffset = int.MinValue;

        /// <summary>
        /// Calls a method the way the generated interop calls it.
        ///
        /// The generated wrappers for these methods build an array of the arguments and go through
        /// `il2cpp_runtime_invoke`; they do not jump to the address in the method's own structure.
        /// That is not a detail this mod is free to improve on — calling the address directly is what
        /// left a `Texture2D` that had run its constructor without becoming a texture, and the next
        /// call on it raised.
        ///
        /// <para>
        /// Each entry of <paramref name="args"/> is a pointer to the argument's own bytes. Not a box:
        /// the invoker dereferences it directly — `Texture2D..ctor`'s reads its five as `**args`,
        /// `*args[1]`, `*args[2]`, and one byte each at `args[3]` and `args[4]` — and
        /// `il2cpp_runtime_invoke` hands the array over untouched. A boxed argument therefore arrives
        /// as the low bytes of the box's class pointer, which is how every texture format asked of
        /// `SystemInfo` came back "not a valid TextureFormat" and how a constructor can run and build
        /// nothing. Reference arguments are unaffected: a reference's value *is* its pointer, so
        /// passing the object is passing the pointer.
        /// </para>
        /// <para>
        /// The return is the other way round — the runtime boxes a value-type result, which is why
        /// the callers read it at <see cref="Offsets.Runtime.BoxedData"/>.
        /// </para>
        /// </summary>
        private static bool Invoke(IntPtr method, IntPtr instance, IntPtr* args, int count, out string reason)
        {
            reason = null;

            for (int i = 0; i < count; i++)
            {
                if (args[i] != IntPtr.Zero) continue;
#if DEBUG
                reason = $"argument {i} could not be built, so the call was not made";
#endif
                return false;
            }

            IntPtr raised = IntPtr.Zero;
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, instance, (void**)args, ref raised);

            if (raised == IntPtr.Zero) return true;

            // Guarded like every other reason in this file, and this one was missed for a while: it
            // is the only `reason` here that is not assigned inside a `#if DEBUG` already, so it was
            // the only one whose text shipped. Nothing in a Release build reads it -- the whole chain
            // from here to the log runs through `Diagnostics.Warn` -- so the literal was a kilobyte
            // of dead text, which is what the artifact check exists to keep out and what it could
            // not catch while this phrase was not on its list. It is on the list now.
#if DEBUG
            reason = $"a call into the game's rendering types raised: {Raised.Text(raised)}";
#endif
            return false;
        }

        /// <summary>
        /// How wide a `Texture2D` says it is, or -1 when it will not say.
        ///
        /// The getter lives on the base `Texture`, so it is looked up there. A width that comes back
        /// as the value the constructor was given is the one piece of evidence available here that a
        /// native texture exists — see <see cref="IsKnownToHaveNoNativeObject"/> for the other.
        /// </summary>
        private static int WidthOf(IntPtr texture)
        {
            IntPtr getter = Entry(_textureClass, "get_width", 0);
            if (getter == IntPtr.Zero)
            {
                Diagnostics.Warn("Texture2D.get_width could not be found, so the built texture cannot be checked");
                return -1;
            }

            IntPtr raised = IntPtr.Zero;
            IntPtr boxed = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(getter, texture, null, ref raised);
            if (raised != IntPtr.Zero || boxed == IntPtr.Zero) return -1;

            return *(int*)(boxed + Offsets.Runtime.BoxedData);
        }

        /// <summary>How many methods a class may report before the walk is treated as broken.</summary>
        private const int MethodsWalkLimit = 4096;

        /// <summary>How many entries of a method walk are logged by name, so a stall is visible.</summary>
        private const int MethodsToLog = 30;
    }
}
