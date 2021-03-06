﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Text;
using ILRuntime.Runtime.Enviorment;

namespace ILRuntime.Runtime.CLRBinding
{
    public class BindingCodeGenerator
    {
        public static void GenerateBindingCode(List<Type> types, string outputPath)
        {
            if (!System.IO.Directory.Exists(outputPath))
                System.IO.Directory.CreateDirectory(outputPath);
            List<string> clsNames = new List<string>();
            foreach (var i in types)
            {
                string clsName, realClsName;
                bool isByRef;
                if (i.GetCustomAttributes(typeof(ObsoleteAttribute), true).Length > 0)
                    continue;
                GetClassName(i, out clsName, out realClsName, out isByRef);
                clsNames.Add(clsName);
                using (System.IO.StreamWriter sw = new System.IO.StreamWriter(outputPath + "/" + clsName + ".cs", false, Encoding.UTF8))
                {
                    sw.Write(@"using System;
using System.Collections.Generic;
using System.Reflection;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Stack;
using ILRuntime.Reflection;
using ILRuntime.CLR.Utils;

namespace ILRuntime.Runtime.Generated
{
    unsafe class ");
                    sw.WriteLine(clsName);
                    sw.Write(@"    {
        public static void Register(ILRuntime.Runtime.Enviorment.AppDomain app)
        {
            BindingFlags flag = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            MethodInfo method;
            Type[] args;
            Type type = typeof(");
                    sw.Write(realClsName);
                    sw.WriteLine(");");
                    MethodInfo[] methods = i.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    string registerCode = GenerateRegisterCode(i, methods);
                    string wraperCode = GenerateWraperCode(i, methods, realClsName);
                    sw.WriteLine(registerCode);
                    sw.WriteLine("        }");
                    sw.WriteLine();
                    sw.WriteLine(wraperCode);
                    sw.WriteLine("    }");
                    sw.WriteLine("}");
                    sw.Flush();
                }
            }

            using (System.IO.StreamWriter sw = new System.IO.StreamWriter(outputPath + "/CLRBindings.cs", false, Encoding.UTF8))
            {
                sw.WriteLine(@"using System;
using System.Collections.Generic;
using System.Reflection;

namespace ILRuntime.Runtime.Generated
{
    class CLRBindings
    {
        /// <summary>
        /// Initialize the CLR binding, please invoke this AFTER CLR Redirection registration
        /// </summary>
        public static void Initialize(ILRuntime.Runtime.Enviorment.AppDomain app)
        {");
                foreach (var i in clsNames)
                {
                    sw.Write("            ");
                    sw.Write(i);
                    sw.WriteLine(".Register(app);");
                }

                sw.WriteLine(@"        }
    }
}");
            }
        }

        static bool ShouldSkipMethod(Type type, MethodInfo i)
        {
            if (i.IsPrivate)
                return true;
            if (i.IsGenericMethod)
                return true;
            //EventHandler is currently not supported
            if (i.IsSpecialName)
            {
                string[] t = i.Name.Split('_');
                if (t[0] == "add" || t[0] == "remove")
                    return true;
                if (t[0] == "get" || t[0] == "set")
                {
                    var prop = type.GetProperty(t[1]);
                    if (prop.GetCustomAttributes(typeof(ObsoleteAttribute), true).Length > 0)
                        return true;
                }
            }
            if (i.GetCustomAttributes(typeof(ObsoleteAttribute), true).Length > 0)
                return true;
            return false;
        }

        static string GenerateRegisterCode(Type type, MethodInfo[] methods)
        {
            StringBuilder sb = new StringBuilder();
            int idx = 0;
            foreach (var i in methods)
            {
                if (ShouldSkipMethod(type, i))
                    continue;
                bool isProperty = i.IsSpecialName;
                var param = i.GetParameters();
                StringBuilder sb2 = new StringBuilder();
                sb2.Append("{");
                bool first = true;
                foreach (var j in param)
                {
                    if (first)
                        first = false;
                    else
                        sb2.Append(", ");
                    sb2.Append("typeof(");
                    string tmp, clsName;
                    bool isByRef;
                    GetClassName(j.ParameterType, out tmp, out clsName, out isByRef);
                    sb2.Append(clsName);
                    sb2.Append(")");
                    if (isByRef)
                        sb2.Append(".MakeByRefType()");
                }
                sb2.Append("}");
                sb.AppendLine(string.Format("            args = new Type[]{0};", sb2));
                sb.AppendLine(string.Format("            method = type.GetMethod(\"{0}\", flag, null, args, null);", i.Name));
                sb.AppendLine(string.Format("            app.RegisterCLRMethodRedirection(method, {0}_{1});", i.Name, idx));

                idx++;
            }
            return sb.ToString();
        }

        static string GenerateWraperCode(Type type, MethodInfo[] methods, string typeClsName)
        {
            StringBuilder sb = new StringBuilder();

            int idx = 0;
            foreach (var i in methods)
            {
                if (ShouldSkipMethod(type, i))
                    continue;
                bool isProperty = i.IsSpecialName;
                var param = i.GetParameters();
                int paramCnt = param.Length;
                if (!i.IsStatic)
                    paramCnt++;
                sb.AppendLine(string.Format("        static StackObject* {0}_{1}(ILIntepreter intp, StackObject* esp, List<object> mStack, CLRMethod __method)", i.Name, idx));
                sb.AppendLine("        {");
                sb.AppendLine("            ILRuntime.Runtime.Enviorment.AppDomain domain = intp.AppDomain;");
                sb.AppendLine("            StackObject* p;");
                sb.AppendLine(string.Format("            StackObject* ret = ILIntepreter.Minus(esp, {0});", paramCnt));
                for (int j = param.Length; j > 0; j--)
                {
                    var p = param[j - 1];
                    sb.AppendLine(string.Format("            p = ILIntepreter.Minus(esp, {0});", param.Length - j + 1));
                    string tmp, clsName;
                    bool isByRef;
                    GetClassName(p.ParameterType, out tmp, out clsName, out isByRef);
                    if (isByRef)
                        sb.AppendLine("            p = ILIntepreter.GetObjectAndResolveReference(p);");
                    sb.AppendLine(string.Format("            {0} {1} = {2};", clsName, p.Name, GetRetrieveValueCode(p.ParameterType, clsName)));
                    if (!isByRef && !p.ParameterType.IsPrimitive)
                        sb.AppendLine("            intp.Free(p);");
                }
                if (!i.IsStatic)
                {
                    sb.AppendLine(string.Format("            p = ILIntepreter.Minus(esp, {0});", paramCnt));
                    string tmp, clsName;
                    bool isByRef;
                    GetClassName(type, out tmp, out clsName, out isByRef);
                    if (type.IsPrimitive || type.IsValueType)
                        sb.AppendLine("            p = ILIntepreter.GetObjectAndResolveReference(p);");
                    sb.AppendLine(string.Format("            {0} instance_of_this_method;", clsName));
                    if (type.IsPrimitive)
                    {
                        sb.Append(@"            switch(p->ObjectType)
            {
                case ObjectTypes.FieldReference:
                    {
                        var instance_of_fieldReference = mStack[p->Value];
                        if(instance_of_fieldReference is ILTypeInstance)
                        {
                            instance_of_this_method = (");
                        sb.Append(clsName);
                        sb.Append(")((ILTypeInstance)instance_of_fieldReference)[p->ValueLow];");
                        sb.Append(@"
                        }
                        else
                        {
                            var t = domain.GetType(instance_of_fieldReference.GetType()) as CLRType;
                            instance_of_this_method = (");
                        sb.Append(clsName);
                        sb.Append(")t.Fields[p->ValueLow].GetValue(instance_of_fieldReference);");
                        sb.AppendLine(@"
                        }
                    }
                    break;
                default:");
                        sb.AppendLine(string.Format("                    instance_of_this_method = {0};", GetRetrieveValueCode(type, clsName)));
                        sb.AppendLine(@"                    break;
            }");
                    }
                    else
                        sb.AppendLine(string.Format("            instance_of_this_method = {0};", GetRetrieveValueCode(type, clsName)));
                    if (!isByRef && !type.IsPrimitive)
                        sb.AppendLine("            intp.Free(p);");
                }
                sb.AppendLine();
                if (i.ReturnType != typeof(void))
                {
                    sb.Append("            var result_of_this_method = ");
                }
                else
                    sb.Append("            ");
                if (i.IsStatic)
                {
                    if (isProperty)
                    {
                        string[] t = i.Name.Split('_');
                        string propType = t[0];

                        if (propType == "get")
                        {
                            bool isIndexer = param.Length > 0;
                            if (isIndexer)
                            {
                                sb.AppendLine(string.Format("{1}[{0}];", param[0].Name, typeClsName));
                            }
                            else
                                sb.AppendLine(string.Format("{1}.{0};", t[1], typeClsName));
                        }
                        else if (propType == "set")
                        {
                            bool isIndexer = param.Length > 1;
                            if (isIndexer)
                            {
                                sb.AppendLine(string.Format("{2}[{0}] = {1};", param[0].Name, param[1].Name, typeClsName));
                            }
                            else
                                sb.AppendLine(string.Format("{2}.{0} = {1};", t[1], param[0].Name, typeClsName));
                        }
                        else if (propType == "op")
                        {
                            switch (t[1])
                            {
                                case "Equality":
                                    sb.AppendLine(string.Format("{0} == {1};", param[0].Name, param[1].Name));
                                    break;
                                case "Inequality":
                                    sb.AppendLine(string.Format("{0} != {1};", param[0].Name, param[1].Name));
                                    break;
                                case "Addition":
                                    sb.AppendLine(string.Format("{0} + {1};", param[0].Name, param[1].Name));
                                    break;
                                case "Subtraction":
                                    sb.AppendLine(string.Format("{0} - {1};", param[0].Name, param[1].Name));
                                    break;
                                case "Multiply":
                                    sb.AppendLine(string.Format("{0} * {1};", param[0].Name, param[1].Name));
                                    break;
                                case "Division":
                                    sb.AppendLine(string.Format("{0} / {1};", param[0].Name, param[1].Name));
                                    break;
                                case "UnaryNegation":
                                    sb.AppendLine(string.Format("-{0};", param[0].Name));
                                    break;
                                default:
                                    throw new NotImplementedException(i.Name);
                            }
                        }
                        else
                            throw new NotImplementedException();
                    }
                    else
                    {
                        sb.Append(string.Format("{0}.{1}(", typeClsName, i.Name));
                        AppendParameters(param, sb);
                        sb.AppendLine(");");
                    }
                }
                else
                {
                    if (isProperty)
                    {
                        string[] t = i.Name.Split('_');
                        string propType = t[0];

                        if (propType == "get")
                        {
                            bool isIndexer = param.Length > 0;
                            if (isIndexer)
                            {
                                sb.AppendLine(string.Format("instance_of_this_method[{0}];", param[0].Name));
                            }
                            else
                                sb.AppendLine(string.Format("instance_of_this_method.{0};", t[1]));
                        }
                        else if (propType == "set")
                        {
                            bool isIndexer = param.Length > 1;
                            if (isIndexer)
                            {
                                sb.AppendLine(string.Format("instance_of_this_method[{0}] = {1};", param[0].Name, param[1].Name));
                            }
                            else
                                sb.AppendLine(string.Format("instance_of_this_method.{0} = {1};", t[1], param[0].Name));
                        }
                        else
                            throw new NotImplementedException();
                    }
                    else
                    {
                        sb.Append(string.Format("instance_of_this_method.{0}(", i.Name));
                        AppendParameters(param, sb);
                        sb.AppendLine(");");
                    }
                }
                sb.AppendLine();
                //Ref/Out
                for (int j = param.Length; j > 0; j--)
                {
                    var p = param[j - 1];
                    if (!p.ParameterType.IsByRef)
                        continue;
                    sb.AppendLine(string.Format("            p = ILIntepreter.Minus(esp, {0});", param.Length - j + 1));
                    sb.AppendLine(@"            switch(p->ObjectType)
            {
                case ObjectTypes.StackObjectReference:
                    {
                        var dst = *(StackObject**)&p->Value;");
                    GetRefWriteBackValueCode(p.ParameterType.GetElementType(), sb, p.Name);
                    sb.Append(@"                    }
                    break;
                case ObjectTypes.FieldReference:
                    {
                        var obj = mStack[p->Value];
                        if(obj is ILTypeInstance)
                        {
                            ((ILTypeInstance)obj)[p->ValueLow] = ");
                    sb.Append(p.Name);
                    sb.Append(@";
                        }
                        else
                        {
                            var t = domain.GetType(obj.GetType()) as CLRType;
                            t.Fields[p->ValueLow].SetValue(obj, ");
                    sb.Append(p.Name);
                    sb.Append(@");
                        }
                    }
                    break;
                case ObjectTypes.StaticFieldReference:
                    {
                        var t = domain.GetType(p->Value);
                        if(t is ILType)
                        {
                            ((ILType)t).StaticInstance[p->ValueLow] = ");
                    sb.Append(p.Name);
                    sb.Append(@";
                        }
                        else
                        {
                            ((CLRType)t).Fields[p->ValueLow].SetValue(null, ");
                    sb.Append(p.Name);
                    sb.AppendLine(@");
                        }
                    }
                    break;
            }");
                    sb.AppendLine();
                }
                if (i.ReturnType != typeof(void))
                {
                    GetReturnValueCode(i.ReturnType, sb);
                }
                else
                    sb.AppendLine("            return ret;");
                sb.AppendLine("        }");
                sb.AppendLine();
                idx++;
            }

            return sb.ToString();
        }

        static void AppendParameters(ParameterInfo[] param, StringBuilder sb)
        {
            bool first = true;
            foreach (var j in param)
            {
                if (first)
                    first = false;
                else
                    sb.Append(", ");
                if (j.IsOut)
                    sb.Append("out ");
                else if (j.ParameterType.IsByRef)
                    sb.Append("ref ");
                sb.Append(j.Name);
            }
        }

        static void GetRefWriteBackValueCode(Type type, StringBuilder sb, string paramName)
        {
            if (type.IsPrimitive)
            {
                if (type == typeof(int))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(long))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Long;");
                    sb.Append("                        *(long*)&dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(short))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(bool))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        dst->Value = " + paramName + " ? 1 : 0;");
                    sb.AppendLine(";");
                }
                else if (type == typeof(ushort))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(float))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Float;");
                    sb.Append("                        *(float*)&dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(double))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Double;");
                    sb.Append("                        *(double*)&dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(byte))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        (byte)dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(sbyte))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        (sbyte)dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(uint))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        dst->Value = (int)" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(char))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        dst->Value = (int)" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(ulong))
                {
                    sb.AppendLine("                        dst->ObjectType = ObjectTypes.Long;");
                    sb.Append("                        *(ulong*)&dst->Value = " + paramName);
                    sb.AppendLine(";");
                }
                else
                    throw new NotImplementedException();
            }
            else
            {
                if (!type.IsValueType)
                {
                    sb.Append(@"                        object obj = ");
                    sb.Append(paramName);
                    sb.AppendLine(";");

                    sb.AppendLine(@"                        if (obj is CrossBindingAdaptorType)
                            obj = ((CrossBindingAdaptorType)obj).ILInstance;
                        mStack[dst->Value] = obj; ");
                }
                else
                {
                    sb.Append("                        mStack[dst->Value] = ");
                    sb.Append(paramName);
                    sb.AppendLine(";");
                }
            }
        }

        static void GetReturnValueCode(Type type, StringBuilder sb)
        {
            if (type.IsPrimitive)
            {
                if (type == typeof(int))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            ret->Value = result_of_this_method;");
                }
                else if (type == typeof(long))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Long;");
                    sb.AppendLine("            *(long*)&ret->Value = result_of_this_method;");
                }
                else if (type == typeof(short))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            ret->Value = result_of_this_method;");
                }
                else if (type == typeof(bool))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            ret->Value = result_of_this_method ? 1 : 0;");
                }
                else if (type == typeof(ushort))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            ret->Value = result_of_this_method;");
                }
                else if (type == typeof(float))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Float;");
                    sb.AppendLine("            *(float*)&ret->Value = result_of_this_method;");
                }
                else if (type == typeof(double))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Double;");
                    sb.AppendLine("            *(double*)&ret->Value = result_of_this_method;");
                }
                else if (type == typeof(byte))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            (byte)ret->Value = result_of_this_method;");
                }
                else if (type == typeof(sbyte))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            (sbyte)ret->Value = result_of_this_method;");
                }
                else if (type == typeof(uint))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            ret->Value = (int)result_of_this_method;");
                }
                else if (type == typeof(char))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            ret->Value = (int)result_of_this_method;");
                }
                else if (type == typeof(ulong))
                {
                    sb.AppendLine("            ret->ObjectType = ObjectTypes.Long;");
                    sb.AppendLine("            *(ulong*)&ret->Value = result_of_this_method;");
                }
                else
                    throw new NotImplementedException();
                sb.AppendLine("            return ret + 1;");

            }
            else
            {
                if (!type.IsSealed)
                {
                    sb.AppendLine(@"            object obj_result_of_this_method = result_of_this_method;
            if(obj_result_of_this_method is CrossBindingAdaptorType)
            {    
                return ILIntepreter.PushObject(ret, mStack, ((CrossBindingAdaptorType)obj_result_of_this_method).ILInstance);
            }");
                }
                sb.AppendLine("            return ILIntepreter.PushObject(ret, mStack, result_of_this_method);");
            }
        }

        static string GetRetrieveValueCode(Type type, string realClsName)
        {
            if (type.IsByRef)
                type = type.GetElementType();
            if (type.IsPrimitive)
            {
                if (type == typeof(int))
                {
                    return "p->Value";
                }
                else if (type == typeof(long))
                {
                    return "*(long*)&p->Value";
                }
                else if (type == typeof(short))
                {
                    return "(short)p->Value";
                }
                else if (type == typeof(bool))
                {
                    return "p->Value == 1";
                }
                else if (type == typeof(ushort))
                {
                    return "(ushort)p->Value";
                }
                else if (type == typeof(float))
                {
                    return "*(float*)&p->Value";
                }
                else if (type == typeof(double))
                {
                    return "*(double*)&p->Value";
                }
                else if (type == typeof(byte))
                {
                    return "(byte)p->Value";
                }
                else if (type == typeof(sbyte))
                {
                    return "(sbyte)p->Value";
                }
                else if (type == typeof(uint))
                {
                    return "(uint)p->Value";
                }
                else if (type == typeof(char))
                {
                    return "(char)p->Value";
                }
                else if (type == typeof(ulong))
                {
                    return "*(ulong*)&p->Value";
                }
                else
                    throw new NotImplementedException();
            }
            else
            {
                return string.Format("({0})typeof({0}).CheckCLRTypes(domain, StackObject.ToObject(p, domain, mStack))", realClsName);
            }
        }

        static void GetClassName(Type type, out string clsName, out string realClsName, out bool isByRef)
        {
            isByRef = type.IsByRef;
            if (isByRef)
                type = type.GetElementType();
            string realNamespace = null;
            if (type.IsNested)
            {
                string bClsName, bRealClsName;
                bool tmp;
                GetClassName(type.ReflectedType, out bClsName, out bRealClsName, out tmp);
                clsName = bClsName + "_";
                realNamespace = bRealClsName + ".";
            }
            else
            {
                clsName = !string.IsNullOrEmpty(type.Namespace) ? type.Namespace.Replace(".", "_") + "_" : "";
                realNamespace = !string.IsNullOrEmpty(type.Namespace) ? type.Namespace + "." : null;
            }
            clsName = clsName + type.Name.Replace(".", "_").Replace("`", "_").Replace("<", "_").Replace(">", "_");
            bool isGeneric = false;
            string ga = null;
            if (type.IsGenericType)
            {
                isGeneric = true;
                clsName += "_";
                ga = "<";
                var args = type.GetGenericArguments();
                bool first = true;
                foreach (var j in args)
                {
                    if (first)
                        first = false;
                    else
                    {
                        clsName += "_";
                        ga += ", ";
                    }
                    clsName += j.Name;
                    string a, b;
                    bool tmp;
                    GetClassName(j, out a, out b, out tmp);
                    ga += b;
                }
                ga += ">";
            }
            clsName += "_Binding";

            realClsName = realNamespace;
            if (isGeneric)
            {
                int idx = type.Name.IndexOf("`");
                realClsName += type.Name.Substring(0, idx);
                realClsName += ga;
            }
            else
                realClsName += type.Name;
        }

    }
}
