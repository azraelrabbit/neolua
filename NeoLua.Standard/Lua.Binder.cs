﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Neo.IronLua
{
  #region -- ILuaBinder ---------------------------------------------------------------

  internal interface ILuaBinder
  {
    Lua Lua { get; }
  } // interface ILuaBinder

  #endregion

  #region -- class Lua ----------------------------------------------------------------

  ///////////////////////////////////////////////////////////////////////////////
  /// <summary></summary>
  public partial class Lua
  {
    #region -- enum BindResult --------------------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary>Result for the binding of methods</summary>
    internal enum BindResult
    {
      Ok,
      MemberNotFound,
      NotReadable,
      NotWriteable
    } // enum BindResult

    #endregion

    #region -- class LuaGetMemberBinder -----------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
		internal class LuaGetMemberBinder : GetMemberBinder, ILuaBinder
    {
      private readonly Lua lua;

      public LuaGetMemberBinder(Lua lua, string name)
        : base(name, false)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackGetMember(DynamicMetaObject target, DynamicMetaObject errorSuggestion)
      {
        // defer the target, to get the type
        if (!target.HasValue)
          return Defer(target);

        if (target.Value == null) // no value for target, finish binding with an error or the suggestion
        {
          return errorSuggestion ??
            new DynamicMetaObject(
              ThrowExpression(Resources.rsNullReference, ReturnType),
              target.Restrictions.Merge(BindingRestrictions.GetInstanceRestriction(target.Expression, null))
            );
        }
        else
        {
					Expression expr;
					
					// restrictions
					var restrictions = target.Restrictions.Merge(BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType));

					// try to bind the member
					switch (LuaEmit.TryGetMember(target.Expression, target.LimitType, Name, IgnoreCase, out expr))
					{
						case LuaTryGetMemberReturn.None:
							return errorSuggestion ?? new DynamicMetaObject(Expression.Default(ReturnType), restrictions);
						case LuaTryGetMemberReturn.NotReadable:
							return errorSuggestion ?? new DynamicMetaObject(ThrowExpression(LuaEmitException.GetMessageText(LuaEmitException.CanNotReadMember, target.LimitType.Name, Name), ReturnType), restrictions);
						case LuaTryGetMemberReturn.ValidExpression:
							return new DynamicMetaObject(Lua.EnsureType(expr, ReturnType), restrictions);
						default:
							throw new ArgumentException("return of TryGetMember.");
					}
        }
      } // func FallbackGetMember

			public Lua Lua => lua;
    } // class LuaGetMemberBinder

    #endregion

    #region -- class LuaSetMemberBinder -----------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
		internal class LuaSetMemberBinder : SetMemberBinder, ILuaBinder
    {
      private readonly Lua lua;

      public LuaSetMemberBinder(Lua lua, string name)
        : base(name, false)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackSetMember(DynamicMetaObject target, DynamicMetaObject value, DynamicMetaObject errorSuggestion)
      {
        // defer the target
        if (!target.HasValue)
          return Defer(target);

				if (target.Value == null)
				{
					return errorSuggestion ??
						new DynamicMetaObject(
							ThrowExpression(String.Format(Resources.rsMemberNotResolved, target.LimitType.Name, Name), ReturnType),
							target.Restrictions.Merge(BindingRestrictions.GetInstanceRestriction(target.Expression, null))
						);
				}
				else
				{
					Expression expr;

					// restrictions
					var restrictions = target.Restrictions.Merge(BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType));

					// try to bind the member
					switch (LuaEmit.TrySetMember(target.Expression, target.LimitType, Name, IgnoreCase,
						(setType) => LuaEmit.ConvertWithRuntime(Lua, value.Expression, value.LimitType, setType),
						out expr))
					{
						case LuaTrySetMemberReturn.None:
							return errorSuggestion ?? new DynamicMetaObject(ThrowExpression(LuaEmitException.GetMessageText(LuaEmitException.MemberNotFound, target.LimitType.Name, Name), ReturnType), restrictions);
						case LuaTrySetMemberReturn.NotWritable:
							return errorSuggestion ?? new DynamicMetaObject(ThrowExpression(LuaEmitException.GetMessageText(LuaEmitException.CanNotWriteMember, target.LimitType.Name, Name), ReturnType), restrictions);
						case LuaTrySetMemberReturn.ValidExpression:
							return new DynamicMetaObject(Lua.EnsureType(expr, ReturnType), restrictions.Merge(Lua.GetSimpleRestriction(value)));
						default:
							throw new ArgumentException("return of TryGetMember.");
					}
				}
      } // func FallbackSetMember

      public Lua Lua => lua;
    } // class LuaSetMemberBinder

    #endregion

    #region -- class LuaGetIndexBinder ------------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
		internal class LuaGetIndexBinder : GetIndexBinder, ILuaBinder
    {
      private readonly Lua lua;

      public LuaGetIndexBinder(Lua lua, CallInfo callInfo)
        : base(callInfo)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackGetIndex(DynamicMetaObject target, DynamicMetaObject[] indexes, DynamicMetaObject errorSuggestion)
      {
        // Defer the parameters
        if (!target.HasValue || indexes.Any(c => !c.HasValue))
          return Defer(target, indexes);

        Expression expr;
        if (target.Value == null)
        {
          if (errorSuggestion != null)
            return errorSuggestion;
          expr = ThrowExpression(Resources.rsNullReference, ReturnType);
        }
        else
          try
          {
            expr = Lua.EnsureType(LuaEmit.GetIndex(lua, target, indexes, mo => mo.Expression, mo => mo.LimitType, false), ReturnType);
          }
          catch (LuaEmitException e)
          {
            if (errorSuggestion != null)
              return errorSuggestion;
            expr = ThrowExpression(e.Message, ReturnType);
          }

        return new DynamicMetaObject(expr, GetMethodSignatureRestriction(target, indexes));
      } // func FallbackGetIndex

      public Lua Lua => lua;
    } // class LuaGetIndexBinder

    #endregion

    #region -- class LuaSetIndexBinder ------------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
		internal class LuaSetIndexBinder : SetIndexBinder, ILuaBinder
    {
      private readonly Lua lua;

      public LuaSetIndexBinder(Lua lua, CallInfo callInfo)
        : base(callInfo)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackSetIndex(DynamicMetaObject target, DynamicMetaObject[] indexes, DynamicMetaObject value, DynamicMetaObject errorSuggestion)
      {
        // Defer the parameters
        if (!target.HasValue || indexes.Any(c => !c.HasValue))
        {
          DynamicMetaObject[] def = new DynamicMetaObject[indexes.Length + 1];
          def[0] = target;
          Array.Copy(indexes, 0, def, 1, indexes.Length);
          return Defer(def);
        }

        Expression expr;
        if (target.Value == null)
        {
          if (errorSuggestion != null)
            return errorSuggestion;
          expr = ThrowExpression(Resources.rsNullReference, ReturnType);
        }
        else
          try
          {
            expr = Lua.EnsureType(LuaEmit.SetIndex(lua, target, indexes, value, mo => mo.Expression, mo => mo.LimitType, false), ReturnType);
          }
          catch (LuaEmitException e)
          {
            if (errorSuggestion != null)
              return errorSuggestion;
            expr = ThrowExpression(e.Message, ReturnType);
          }

        return new DynamicMetaObject(expr, GetMethodSignatureRestriction(target, indexes).Merge(Lua.GetSimpleRestriction(value)));
      } // func FallbackSetIndex

			public Lua Lua => lua;
    } // class LuaSetIndexBinder

    #endregion

    #region -- class LuaInvokeBinder --------------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
    internal class LuaInvokeBinder : InvokeBinder, ILuaBinder
    {
      private readonly Lua lua;

      public LuaInvokeBinder(Lua lua, CallInfo callInfo)
        : base(callInfo)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackInvoke(DynamicMetaObject target, DynamicMetaObject[] args, DynamicMetaObject errorSuggestion)
      {
        //defer the target and all arguments
        if (!target.HasValue || args.Any(c => !c.HasValue))
          return Defer(target, args);

				if (target.Value == null) // Invoke on null value
				{
					return errorSuggestion ??
						new DynamicMetaObject(
							ThrowExpression(Resources.rsNilNotCallable),
							BindingRestrictions.GetInstanceRestriction(target.Expression, null)
					);
				}
				else
				{
					var restrictions = GetMethodSignatureRestriction(target, args);
					Expression expr;
					var invokeTarget = target.Value as Delegate;

					if (invokeTarget == null)
					{
						if (errorSuggestion != null)
							return errorSuggestion;
						expr = ThrowExpression(LuaEmitException.GetMessageText(LuaEmitException.InvokeNoDelegate, target.LimitType.Name), typeof(object));
					}
					else
					{
						ParameterInfo[] methodParameters = invokeTarget.GetMethodInfo().GetParameters();
						ParameterInfo[] parameters = null;
						MethodInfo mi = target.LimitType.GetTypeInfo().FindDeclaredMethod("Invoke", ReflectionFlag.Public | ReflectionFlag.Instance | ReflectionFlag.NoException | ReflectionFlag.NoArguments);
						if (mi != null)
						{
							var typeParameters = mi.GetParameters();
							if (typeParameters.Length != methodParameters.Length)
							{
								parameters = new ParameterInfo[typeParameters.Length];
								
								// the hidden parameters are normally at the beginning
								if (parameters.Length > 0)
									Array.Copy(methodParameters, methodParameters.Length - typeParameters.Length, parameters, 0, parameters.Length);
							}
							else
								parameters = methodParameters;
						}
						else
							parameters = methodParameters;

						try
						{
							expr = EnsureType(
								LuaEmit.BindParameter(lua,
									_args => Expression.Invoke(EnsureType(target.Expression, target.LimitType), _args),
									parameters,
									CallInfo,
									args,
									mo => mo.Expression, mo => mo.LimitType, false),
								typeof(object), true
							);
						}
						catch (LuaEmitException e)
						{
							if (errorSuggestion != null)
								return errorSuggestion;
							expr = ThrowExpression(e.Message, ReturnType);
						}
					}

					return new DynamicMetaObject(expr, restrictions);
				}
      } // func FallbackInvoke

      public Lua Lua => lua;
    } // class LuaInvokeBinder

    #endregion

    #region -- class LuaInvokeMemberBinder --------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
    internal class LuaInvokeMemberBinder : InvokeMemberBinder, ILuaBinder
    {
      private readonly Lua lua;

      public LuaInvokeMemberBinder(Lua lua, string name, CallInfo callInfo)
        : base(name, false, callInfo)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackInvoke(DynamicMetaObject target, DynamicMetaObject[] args, DynamicMetaObject errorSuggestion)
      {
				var binder = (LuaInvokeBinder)lua.GetInvokeBinder(CallInfo);
        return binder.Defer(target, args);
      } // func FallbackInvoke

      public override DynamicMetaObject FallbackInvokeMember(DynamicMetaObject target, DynamicMetaObject[] args, DynamicMetaObject errorSuggestion)
      {
        // defer target and all arguments
        if (!target.HasValue || args.Any(c => !c.HasValue))
          return Defer(target, args);

				if (target.Value == null)
				{
					return errorSuggestion ??
						new DynamicMetaObject(
							ThrowExpression(Resources.rsNilNotCallable, ReturnType),
							target.Restrictions.Merge(BindingRestrictions.GetInstanceRestriction(target.Expression, null))
						);
				}
				else
				{
					try
					{
						var luaType = LuaType.GetType(target.LimitType);
						Expression expr;
						if (LuaEmit.TryInvokeMember<DynamicMetaObject>(lua, luaType, target, CallInfo, args, Name, IgnoreCase, mo => mo.Expression, mo => mo.LimitType, false, out expr))
						{
							return new DynamicMetaObject(Lua.EnsureType(expr, ReturnType), GetMethodSignatureRestriction(target, args));
						}
						else
						{
							return errorSuggestion ??
								new DynamicMetaObject
									(ThrowExpression(String.Format(Resources.rsMemberNotResolved, luaType.FullName, Name), ReturnType),
									GetMethodSignatureRestriction(target, args)
								);
						}
					}
					catch (LuaEmitException e)
					{
						return errorSuggestion ??
							new DynamicMetaObject(ThrowExpression(e.Message, ReturnType), GetMethodSignatureRestriction(target, args));
					}
				}
      } // func FallbackInvokeMember

      public Lua Lua => lua;
    } // class LuaInvokeMemberBinder

    #endregion

    #region -- class LuaBinaryOperationBinder -----------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
    internal class LuaBinaryOperationBinder : BinaryOperationBinder, ILuaBinder
    {
      private readonly Lua lua;
      private readonly bool isInteger;

      public LuaBinaryOperationBinder(Lua lua, ExpressionType operation, bool isInteger)
        : base(operation)
      {
        this.lua = lua;
        this.isInteger = isInteger;
      } // ctor

      public override DynamicMetaObject FallbackBinaryOperation(DynamicMetaObject target, DynamicMetaObject arg, DynamicMetaObject errorSuggestion)
      {
        // defer target and all arguments
        if (!target.HasValue || !arg.HasValue)
          return Defer(target, arg);

        Expression expr;
        try
        {
          expr = EnsureType(LuaEmit.BinaryOperationExpression(lua, 
            isInteger && Operation == ExpressionType.Divide ? Lua.IntegerDivide : Operation, 
            target.Expression, target.LimitType, 
            arg.Expression, arg.LimitType, false), this.ReturnType);
        }
        catch (LuaEmitException e)
        {
          if (errorSuggestion != null)
            return errorSuggestion;
          expr = ThrowExpression(e.Message, this.ReturnType);
        }

        // restrictions
        var restrictions = target.Restrictions
          .Merge(arg.Restrictions)
          .Merge(Lua.GetSimpleRestriction(target))
          .Merge(Lua.GetSimpleRestriction(arg));

        return new DynamicMetaObject(expr, restrictions);
      } // func FallbackBinaryOperation

      public Lua Lua => lua;
      public bool IsInteger => isInteger;
    } // class LuaBinaryOperationBinder

    #endregion

    #region -- class LuaUnaryOperationBinder ------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
		internal class LuaUnaryOperationBinder : UnaryOperationBinder, ILuaBinder
    {
      private readonly Lua lua;

      public LuaUnaryOperationBinder(Lua lua, ExpressionType operation)
        : base(operation)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackUnaryOperation(DynamicMetaObject target, DynamicMetaObject errorSuggestion)
      {
        // defer the target
        if (!target.HasValue)
          return Defer(target);

        if (target.Value == null)
        {
          return errorSuggestion ??
            new DynamicMetaObject(
              ThrowExpression(Resources.rsNilOperatorError),
              target.Restrictions.Merge(BindingRestrictions.GetInstanceRestriction(target.Expression, null))
            );
        }
        else
        {
          Expression expr;
          try
          {
            expr = EnsureType(LuaEmit.UnaryOperationExpression(lua, Operation, target.Expression, target.LimitType, false), ReturnType);
          }
          catch (LuaEmitException e)
          {
            if (errorSuggestion != null)
              return errorSuggestion;
            expr = ThrowExpression(e.Message, this.ReturnType);
          }

          var restrictions = target.Restrictions.Merge(BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType));
          return new DynamicMetaObject(expr, restrictions);
        }
      } // func FallbackUnaryOperation

      public Lua Lua => lua;
    } // class LuaUnaryOperationBinder

    #endregion

    #region -- class LuaConvertBinder -------------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
    internal class LuaConvertBinder : ConvertBinder, ILuaBinder
    {
      private readonly Lua lua;

			public LuaConvertBinder(Lua lua, Type toType)
        : base(toType, false)
      {
        this.lua = lua;
      } // ctor

      public override DynamicMetaObject FallbackConvert(DynamicMetaObject target, DynamicMetaObject errorSuggestion)
      {
        if (!target.HasValue)
          return Defer(target);

				if (target.Value == null) // get the default value
				{
					Expression expr;
					var restrictions = target.Restrictions.Merge(BindingRestrictions.GetInstanceRestriction(target.Expression, null));

					if (Type == typeof(LuaResult)) // replace null with empty LuaResult 
						expr = Expression.Property(null, Lua.ResultEmptyPropertyInfo);
					else if (Type == typeof(string)) // replace null with empty String
						expr = Expression.Field(null, Lua.StringEmptyFieldInfo);
					else
						expr = Expression.Default(Type);

					return new DynamicMetaObject(Lua.EnsureType(expr, ReturnType), restrictions);
				}
				else // convert the value
				{
					var restrictions = target.Restrictions.Merge(BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType));
					object result;
					if (LuaEmit.TryConvert(target.Expression, target.LimitType, Type, null, out result))
					{
						return new DynamicMetaObject(Lua.EnsureType((Expression)result, ReturnType), restrictions);
					}
					else if (errorSuggestion == null)
					{
						if (result == null)
							throw new ArgumentNullException("expr", "LuaEmit.TryConvert does not return a expression.");
						return new DynamicMetaObject(ThrowExpression(((LuaEmitException)result).Message, ReturnType), restrictions);
					}
					else
						return errorSuggestion;
				}
      } // func FallbackConvert

      public Lua Lua => lua;
    } // class LuaConvertBinder

    #endregion

    #region -- class MemberCallInfo ---------------------------------------------------

    ///////////////////////////////////////////////////////////////////////////////
    /// <summary></summary>
    private class MemberCallInfo
    {
      private string sMember;
      private CallInfo ci;

      public MemberCallInfo(string sMember, CallInfo ci)
      {
        this.sMember = sMember;
        this.ci = ci;
      } // ctor

      public override int GetHashCode()
      {
        return 0x28000000 ^ sMember.GetHashCode() ^ ci.GetHashCode();
      } // func GetHashCode

      public override bool Equals(object obj)
      {
        MemberCallInfo mci = obj as MemberCallInfo;
        return mci != null && mci.sMember == sMember && mci.ci.Equals(ci);
      } // func Equals

			public override string ToString()
			{
				return sMember + "#" + ci.ArgumentCount.ToString();
			}
    } // struct MemberCallInfo

    #endregion

    #region -- Binder Cache -----------------------------------------------------------

    private Dictionary<ExpressionType, CallSiteBinder> operationBinder = new Dictionary<ExpressionType, CallSiteBinder>();
    private Dictionary<string, CallSiteBinder> getMemberBinder = new Dictionary<string, CallSiteBinder>();
    private Dictionary<string, CallSiteBinder> setMemberBinder = new Dictionary<string, CallSiteBinder>();
    private Dictionary<CallInfo, CallSiteBinder> getIndexBinder = new Dictionary<CallInfo, CallSiteBinder>();
    private Dictionary<CallInfo, CallSiteBinder> setIndexBinder = new Dictionary<CallInfo, CallSiteBinder>();
    private Dictionary<CallInfo, CallSiteBinder> invokeBinder = new Dictionary<CallInfo, CallSiteBinder>();
    private Dictionary<MemberCallInfo, CallSiteBinder> invokeMemberBinder = new Dictionary<MemberCallInfo, CallSiteBinder>();
    private Dictionary<Type, CallSiteBinder> convertBinder = new Dictionary<Type, CallSiteBinder>();

    private void ClearBinderCache()
    {
      lock (operationBinder)
        operationBinder.Clear();
      lock (getMemberBinder)
        getMemberBinder.Clear();
      lock (setMemberBinder)
        setMemberBinder.Clear();
      lock (getIndexBinder)
        getIndexBinder.Clear();
      lock (setIndexBinder)
        setIndexBinder.Clear();
      lock (invokeBinder)
        invokeBinder.Clear();
			lock (invokeMemberBinder)
				invokeMemberBinder.Clear();
			lock (convertBinder)
				convertBinder.Clear();
		} // proc ClearBinderCache

		/// <summary>Writes the content of the rule cache to a file. For debug-reasons.</summary>
		/// <param name="tw"></param>
		public void DumpRuleCaches(TextWriter tw)
		{
			string sSep = new string('=', 66);

			FieldInfo fiCache = typeof(CallSiteBinder).GetTypeInfo().FindDeclaredField("Cache", ReflectionFlag.NonPublic | ReflectionFlag.Instance);

			tw.WriteLine(sSep);
			tw.WriteLine("= Operation Binders");
			DumpRuleCache<ExpressionType>(tw, operationBinder, fiCache);
			tw.WriteLine();

			tw.WriteLine(sSep);
			tw.WriteLine("= GetMember Binders");
			DumpRuleCache<string>(tw, getMemberBinder, fiCache);
			tw.WriteLine();

			tw.WriteLine(sSep);
			tw.WriteLine("= SetMember Binders");
			DumpRuleCache<string>(tw, setMemberBinder, fiCache);
			tw.WriteLine();

			tw.WriteLine(sSep);
			tw.WriteLine("= Get Index Binders");
			DumpRuleCache<CallInfo>(tw, getIndexBinder, fiCache);
			tw.WriteLine();

			tw.WriteLine(sSep);
			tw.WriteLine("= Set Index Binders");
			DumpRuleCache<CallInfo>(tw, setIndexBinder, fiCache);
			tw.WriteLine();

			tw.WriteLine(sSep);
			tw.WriteLine("= Invoke Binders");
			DumpRuleCache<CallInfo>(tw, invokeBinder, fiCache);
			tw.WriteLine();

			tw.WriteLine(sSep);
			tw.WriteLine("= Invoke Member Binders");
			DumpRuleCache<MemberCallInfo>(tw, invokeMemberBinder, fiCache);
			tw.WriteLine();

			tw.WriteLine(sSep);
			tw.WriteLine("= Convert Binders");
			DumpRuleCache<Type>(tw, convertBinder, fiCache);
			tw.WriteLine();
		} // proc DumpRuleCaches

		private void DumpRuleCache<T>(TextWriter tw,  Dictionary<T, CallSiteBinder> binder, FieldInfo fiCache)
		{
			lock (binder)
			{
				foreach (var c in binder)
				{
					object k = c.Key;
					string sKey = typeof(CallInfo) == typeof(T) ? "Args" + ((CallInfo)k).ArgumentCount.ToString() : k.ToString();

					// get the cache
					Dictionary<Type, object> cache = (Dictionary<Type, object>)fiCache.GetValue(c.Value);
					if (cache == null)
						continue;

					foreach (var a in cache)
					{
						Type t = a.Value.GetType();
						Array rules = (Array)t.GetTypeInfo().FindDeclaredField("_rules", ReflectionFlag.Instance | ReflectionFlag.NonPublic).GetValue(a.Value);
						tw.WriteLine(String.Format("{0}: {1}", sKey, rules.Length));
						//for (int i = 0; i < rules.Length; i++)
						//{
						//	object r = rules.GetValue(i);
						//	if (r != null)
						//		tw.WriteLine("  {0}", r.GetType());
						//}
					}
				}
			}
		} // proc DumpRuleCache

    internal CallSiteBinder GetSetMemberBinder(string sName)
    {
      CallSiteBinder b;
      lock (setMemberBinder)
        if (!setMemberBinder.TryGetValue(sName, out b))
          b = setMemberBinder[sName] = new LuaSetMemberBinder(this, sName);
      return b;
    } // func GetSetMemberBinder

    internal CallSiteBinder GetGetMemberBinder(string sName)
    {
      CallSiteBinder b;
      lock (getMemberBinder)
        if (!getMemberBinder.TryGetValue(sName, out b))
          b = getMemberBinder[sName] = new LuaGetMemberBinder(this, sName);
      return b;
    } // func GetGetMemberBinder

    internal CallSiteBinder GetGetIndexMember(CallInfo callInfo)
    {
      CallSiteBinder b;
      lock (getIndexBinder)
        if (!getIndexBinder.TryGetValue(callInfo, out b))
          b = getIndexBinder[callInfo] = new LuaGetIndexBinder(this, callInfo);
      return b;
    } // func GetGetIndexMember

    internal CallSiteBinder GetSetIndexMember(CallInfo callInfo)
    {
      CallSiteBinder b;
      lock (setIndexBinder)
        if (!setIndexBinder.TryGetValue(callInfo, out b))
          b = setIndexBinder[callInfo] = new LuaSetIndexBinder(this, callInfo);
      return b;
    } // func GetSetIndexMember

    internal CallSiteBinder GetInvokeBinder(CallInfo callInfo)
    {
      CallSiteBinder b;
      lock (invokeBinder)
        if (!invokeBinder.TryGetValue(callInfo, out b))
          b = invokeBinder[callInfo] = new LuaInvokeBinder(this, callInfo);
      return b;
    } // func GetInvokeBinder

    internal CallSiteBinder GetInvokeMemberBinder(string sMember, CallInfo callInfo)
    {
      CallSiteBinder b;
      MemberCallInfo mci = new MemberCallInfo(sMember, callInfo);
      lock (invokeMemberBinder)
        if (!invokeMemberBinder.TryGetValue(mci, out b))
          b = invokeMemberBinder[mci] = new LuaInvokeMemberBinder(this, sMember, callInfo);
      return b;
    } // func GetInvokeMemberBinder

    internal CallSiteBinder GetBinaryOperationBinder(ExpressionType expressionType)
    {
      CallSiteBinder b;
      lock (operationBinder)
        if (!operationBinder.TryGetValue(expressionType, out b))
          b = operationBinder[expressionType] =
            expressionType == IntegerDivide ?
              new LuaBinaryOperationBinder(this, ExpressionType.Divide, true) :
              new LuaBinaryOperationBinder(this, expressionType, false);
      return b;
    } // func GetBinaryOperationBinder

    internal CallSiteBinder GetUnaryOperationBinary(ExpressionType expressionType)
    {
      CallSiteBinder b;
      lock (operationBinder)
        if (!operationBinder.TryGetValue(expressionType, out b))
          b = operationBinder[expressionType] = new LuaUnaryOperationBinder(this, expressionType);
      return b;
    } // func GetUnaryOperationBinary

    internal ConvertBinder GetConvertBinder(Type type)
    {
      CallSiteBinder b;
      lock (convertBinder)
        if (!convertBinder.TryGetValue(type, out b))
          b = convertBinder[type] = new LuaConvertBinder(this, type);
      return (ConvertBinder)b;
    } // func GetConvertBinder

    #endregion

    #region -- Binder Expression Helper -----------------------------------------------

    internal static Lua GetRuntime(object v)
    {
      var a = v as ILuaBinder;
      return a != null ? a.Lua : null;
    } // func GetRuntime

    internal static BindingRestrictions GetMethodSignatureRestriction(DynamicMetaObject target, DynamicMetaObject[] args)
    {
      BindingRestrictions restrictions = BindingRestrictions.Combine(args);
      if (target != null)
      {
        restrictions = restrictions
          .Merge(target.Restrictions)
          .Merge(GetSimpleRestriction(target));
      }

      for (int i = 0; i < args.Length; i++)
        restrictions = restrictions.Merge(GetSimpleRestriction(args[i]));

      return restrictions;
    } // func GetMethodSignatureRestriction

    internal static BindingRestrictions GetSimpleRestriction(DynamicMetaObject mo)
    {
      if (mo.HasValue && mo.Value == null)
        return BindingRestrictions.GetInstanceRestriction(mo.Expression, null);
      else
        return BindingRestrictions.GetTypeRestriction(mo.Expression, mo.LimitType);
    } // func GetSimpleRestriction
		
		internal static Expression ThrowExpression(string sMessage, Type type = null)
		{
			return Expression.Throw(
				 Expression.New(
					 Lua.RuntimeExceptionConstructorInfo,
					 Expression.Constant(sMessage, typeof(string)),
					 Expression.Constant(null, typeof(Exception))
				 ),
				 type ?? typeof(object)
			 );
		} // func ThrowExpression

		public static Expression EnsureType(Expression expr, Type returnType, bool forResult = false)
    {
      if (expr.Type == returnType)
        return expr;
      else if (expr.Type == typeof(void))
        if (forResult)
          return Expression.Block(expr, Expression.Property(null, Lua.ResultEmptyPropertyInfo));
        else
          return Expression.Block(expr, Expression.Default(returnType));
      else
        return Expression.Convert(expr, returnType);
    } // func EnsureType

		public static Expression EnsureType(Expression expr, Type exprType, Type returnType, bool forResult = false)
		{
			if (expr.Type != exprType)
				expr = Expression.Convert(expr, exprType);
			return EnsureType(expr, returnType, forResult);
		} // func Expression

		#endregion
	} // class Lua

	#endregion
}
