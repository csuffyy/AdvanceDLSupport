﻿using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace AdvancedDLSupport.ImplementationGenerators
{
    /// <summary>
    /// Generates implementations for properties.
    /// </summary>
    internal class PropertyImplementationGenerator : ImplementationGeneratorBase<PropertyInfo>
    {
        private const MethodAttributes PropertyMethodAttributes =
            MethodAttributes.PrivateScope |
            MethodAttributes.Public |
            MethodAttributes.Virtual |
            MethodAttributes.HideBySig |
            MethodAttributes.VtableLayoutMask |
            MethodAttributes.SpecialName;

        /// <summary>
        /// Initializes a new instance of the <see cref="PropertyImplementationGenerator"/> class.
        /// </summary>
        /// <param name="targetModule">The module in which the property implementation should be generated.</param>
        /// <param name="targetType">The type in which the property implementation should be generated.</param>
        /// <param name="targetTypeConstructorIL">The IL generator for the target type's constructor.</param>
        /// <param name="configuration">The configuration object to use.</param>
        public PropertyImplementationGenerator
        (
            [NotNull] ModuleBuilder targetModule,
            [NotNull] TypeBuilder targetType,
            [NotNull] ILGenerator targetTypeConstructorIL,
            ImplementationConfiguration configuration
        )
            : base(targetModule, targetType, targetTypeConstructorIL, configuration)
        {
        }

        /// <inheritdoc />
        protected override void GenerateImplementation(PropertyInfo property, string symbolName, string uniqueMemberIdentifier)
        {
            // Note, the field is going to have to be a pointer, because it is pointing to global variable
            var fieldType = Configuration.UseLazyBinding ? typeof(Lazy<IntPtr>) : typeof(IntPtr);
            var propertyFieldBuilder = TargetType.DefineField
            (
                uniqueMemberIdentifier,
                fieldType,
                FieldAttributes.Private
            );

            var propertyBuilder = TargetType.DefineProperty
            (
                property.Name,
                PropertyAttributes.None,
                CallingConventions.HasThis,
                property.PropertyType,
                property.GetIndexParameters().Select(p => p.ParameterType).ToArray()
            );

            if (property.CanRead)
            {
                GeneratePropertyGetter(property, propertyFieldBuilder, propertyBuilder);
            }

            if (property.CanWrite)
            {
                GeneratePropertySetter(property, propertyFieldBuilder, propertyBuilder);
            }

            PropertyInitializationInConstructor(symbolName, propertyFieldBuilder); // This is ok for all 3 types of properties.
        }

        private void PropertyInitializationInConstructor
        (
            [NotNull] string symbolName,
            [NotNull] FieldInfo propertyFieldBuilder
        )
        {
            var loadSymbolMethod = typeof(AnonymousImplementationBase).GetMethod
            (
                "LoadSymbol",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            TargetTypeConstructorIL.Emit(OpCodes.Ldarg_0);
            TargetTypeConstructorIL.Emit(OpCodes.Ldarg_0);

            if (Configuration.UseLazyBinding)
            {
                var lambdaBuilder = GenerateSymbolLoadingLambda(symbolName);
                GenerateLazyLoadedField(lambdaBuilder, typeof(IntPtr));
            }
            else
            {
                TargetTypeConstructorIL.Emit(OpCodes.Ldstr, symbolName);
                TargetTypeConstructorIL.EmitCall(OpCodes.Call, loadSymbolMethod, null);
            }

            TargetTypeConstructorIL.Emit(OpCodes.Stfld, propertyFieldBuilder);
        }

        private MethodBuilder GeneratePropertySetter
        (
            [NotNull] PropertyInfo property,
            [NotNull] FieldInfo propertyFieldBuilder,
            [NotNull] PropertyBuilder propertyBuilder
        )
        {
            var actualSetMethod = property.GetSetMethod();
            var setterMethod = TargetType.DefineMethod
            (
                actualSetMethod.Name,
                PropertyMethodAttributes,
                actualSetMethod.CallingConvention,
                typeof(void),
                actualSetMethod.GetParameters().Select(p => p.ParameterType).ToArray()
            );

            MethodInfo underlyingMethod;
            if (property.PropertyType.IsPointer)
            {
                underlyingMethod = typeof(Marshal).GetMethods().First
                (
                    m =>
                        m.Name == nameof(Marshal.WriteIntPtr) &&
                        m.GetParameters().Length == 3
                );
            }
            else if (property.PropertyType.IsValueType)
            {
                underlyingMethod = typeof(Marshal).GetMethods().First
                (
                    m =>
                        m.Name == nameof(Marshal.StructureToPtr) &&
                        m.GetParameters().Length == 3 &&
                        m.IsGenericMethod
                )
                .MakeGenericMethod(property.PropertyType);
            }
            else
            {
                throw new NotSupportedException(
                    string.Format
                    (
                        "{0} Type is not supported. Only ValueType property or Pointer Property is supported.",
                        property.PropertyType.FullName
                    )
                );
            }

            var setterIL = setterMethod.GetILGenerator();

            if (Configuration.GenerateDisposalChecks)
            {
                EmitDisposalCheck(setterIL);
            }

            if (property.PropertyType.IsPointer)
            {
                var explicitConvertToIntPtrFunc = typeof(IntPtr).GetMethods().First
                (
                    m =>
                        m.Name == "op_Explicit"
                );

                GenerateSymbolPush(setterIL, propertyFieldBuilder); // Push Symbol address to stack
                setterIL.Emit(OpCodes.Ldc_I4, 0);                   // Push 0 offset to stack

                setterIL.Emit(OpCodes.Ldarg_1);                     // Push value to stack
                setterIL.EmitCall(OpCodes.Call, explicitConvertToIntPtrFunc, null); // Explicit Convert Pointer to IntPtr object
            }
            else
            {
                setterIL.Emit(OpCodes.Ldarg_1);
                GenerateSymbolPush(setterIL, propertyFieldBuilder);
                setterIL.Emit(OpCodes.Ldc_I4, 0); // false for deleting structure that is already stored in pointer
            }

            setterIL.EmitCall
            (
                OpCodes.Call,
                underlyingMethod,
                null
            );

            setterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetSetMethod(setterMethod);
            this.TargetType.DefineMethodOverride(setterMethod, actualSetMethod);

            return setterMethod;
        }

        private MethodBuilder GeneratePropertyGetter
        (
            [NotNull] PropertyInfo property,
            [NotNull] FieldInfo propertyFieldBuilder,
            [NotNull] PropertyBuilder propertyBuilder
        )
        {
            var actualGetMethod = property.GetGetMethod();
            var getterMethod = TargetType.DefineMethod
            (
                actualGetMethod.Name,
                PropertyMethodAttributes,
                actualGetMethod.CallingConvention,
                actualGetMethod.ReturnType,
                Type.EmptyTypes
            );

            MethodInfo underlyingMethod;
            if (property.PropertyType.IsPointer)
            {
                underlyingMethod = typeof(Marshal).GetMethods().First
                (
                    m =>
                        m.Name == nameof(Marshal.ReadIntPtr) &&
                        m.GetParameters().Length == 1
                );
            }
            else if (property.PropertyType.IsValueType)
            {
                underlyingMethod = typeof(Marshal).GetMethods().First
                (
                    m =>
                        m.Name == nameof(Marshal.PtrToStructure) &&
                        m.GetParameters().Length == 1 &&
                        m.IsGenericMethod
                )
                .MakeGenericMethod(property.PropertyType);
            }
            else
            {
                throw new NotSupportedException(
                    string.Format
                    (
                        "{0} Type is not supported. Only ValueType property or Pointer Property is supported.",
                        property.PropertyType.FullName
                    )
                );
            }

            var getterIL = getterMethod.GetILGenerator();

            if (Configuration.GenerateDisposalChecks)
            {
                EmitDisposalCheck(getterIL);
            }

            GenerateSymbolPush(getterIL, propertyFieldBuilder);

            getterIL.EmitCall
            (
                OpCodes.Call,
                underlyingMethod,
                null
            );

            getterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetGetMethod(getterMethod);
            this.TargetType.DefineMethodOverride(getterMethod, actualGetMethod);

            return getterMethod;
        }
    }
}
