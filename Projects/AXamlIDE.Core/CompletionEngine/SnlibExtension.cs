using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using dnlib.DotNet;

namespace AXamlIDE.CompletionEngine;
    
    internal static class DnlibExtension
    {
        /// <param name="property">The property.</param>
        extension(PropertyDef property)
        {
            /// <summary>
            /// Determines whether the specified property is public.
            /// </summary>
            /// <returns><c>true</c> if the specified property is public; otherwise, <c>false</c>.</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsPublic()
            {
                return property.GetMethod.IsPublic()
                       || property.SetMethod.IsPublic()
                       || property.OtherMethods?.Any(method => method.IsPublic()) == true;
            }

            /// <summary>
            /// Determines whether the specified property is internal.
            /// </summary>
            /// <returns></returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsInternal()
            {
                return property.GetMethod is { IsPrivate: true, Access: MethodAttributes.Assembly }
                       || property.SetMethod is { IsPrivate: true, Access: MethodAttributes.Assembly }
                       || property.OtherMethods?.Any(method => method is { IsPrivate: true, Access: MethodAttributes.Assembly }) == true;
            }
        }

        /// <param name="evt">The event.</param>
        extension(EventDef evt)
        {
            /// <summary>
            /// Determines whether the specified event is public.
            /// </summary>
            /// <returns><c>true</c> if the specified event is public; otherwise, <c>false</c>.</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsPublic()
            {
                return evt.AddMethod?.IsPublic == true
                       || evt.RemoveMethod?.IsPublic == true
                       || evt.InvokeMethod?.IsPublic == true
                       || evt.OtherMethods?.Any(method => method.IsPublic) == true;
            }

            /// <summary>
            /// Determines whether the specified event is internal.
            /// </summary>
            /// <returns><c>true</c> if the specified event is internal; otherwise, <c>false</c>.</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsInternal()
            {
                return evt.AddMethod is { IsPrivate: true, Access: MethodAttributes.Assembly }
                       || evt.RemoveMethod is { IsPrivate: true, Access: MethodAttributes.Assembly }
                       || evt.InvokeMethod is { IsPrivate: true, Access: MethodAttributes.Assembly }
                       || evt.OtherMethods?.Any(method => method is { IsPrivate: true, Access: MethodAttributes.Assembly }) == true;
            }
        }

        extension(MethodDef methodDef)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsPublic()
                => methodDef?.IsPublic == true;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool IsPublicOrInternal()
                => methodDef?.IsPublic == true || methodDef?.Access == MethodAttributes.Assembly;
        }

        public static IEnumerable<string> GetVisibleTo(this AssemblyDef assemblyDef)
        {
            var result = assemblyDef.CustomAttributes
                     .Where(att => att.TypeFullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
                     .Select(att => att.ConstructorArguments[0].Value.ToString())
                     .Where(val => val is not null)
                     .ToArray();
            return result!;
        }
    }