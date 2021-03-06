﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Diagnostics;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.Scripting;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;

using IronPython.Runtime.Operations;
using IronPython.Runtime.Types;

using Ast = System.Linq.Expressions.Expression;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronPython.Runtime.Binding {

    partial class MetaUserObject : MetaPythonObject, IPythonGetable {
        #region IPythonGetable Members

        public DynamicMetaObject GetMember(PythonGetMemberBinder/*!*/ member, Expression/*!*/ codeContext) {
            return GetMemberWorker(member, codeContext);
        }        

        #endregion

        #region MetaObject Overrides

        public override DynamicMetaObject/*!*/ BindGetMember(GetMemberBinder/*!*/ action) {
            return GetMemberWorker(action, BinderState.GetCodeContext(action));
        }

        public override DynamicMetaObject/*!*/ BindSetMember(SetMemberBinder/*!*/ action, DynamicMetaObject/*!*/ value) {
            DynamicMetaObject self = Restrict(Value.GetType());
            CodeContext context = BinderState.GetBinderState(action).Context;
            IPythonObject sdo = Value;
            SetBindingInfo bindingInfo = new SetBindingInfo(
                action,
                new DynamicMetaObject[] { this, value },
                new ConditionalBuilder(action),
                BindingHelpers.GetValidationInfo(this, sdo.PythonType)
            );

            DynamicMetaObject res = null;
            // call __setattr__ if it exists
            PythonTypeSlot dts;
            if (sdo.PythonType.TryResolveSlot(context, Symbols.SetAttr, out dts) && !IsStandardObjectMethod(dts)) {
                // skip the fake __setattr__ on mixed new-style/old-style types
                if (dts != null) {
                    MakeSetAttrTarget(bindingInfo, sdo, dts);
                    res = bindingInfo.Body.GetMetaObject(this, value);
                }
            }

            if (res == null) {
                // then see if we have a set descriptor
                bool isOldStyle;
                bool systemTypeResolution;
                dts = FindSlot(context, action.Name, sdo, out isOldStyle, out systemTypeResolution);
                
                ReflectedSlotProperty rsp = dts as ReflectedSlotProperty;
                if (rsp != null) {
                    MakeSlotsSetTarget(bindingInfo, rsp, value.Expression);
                    res = bindingInfo.Body.GetMetaObject(this, value);
                } else if (dts != null && dts.IsSetDescriptor(context, sdo.PythonType)) {
                    if (systemTypeResolution) {
                        res = Fallback(action, value);
                    } else {
                        res = MakeSlotSet(bindingInfo, dts);
                    }
                }
            }

            if (res == null) {
                // finally if we have a dictionary set the value there.
                if (sdo.PythonType.HasDictionary) {
                    MakeDictionarySetTarget(bindingInfo);
                } else {
                    bindingInfo.Body.FinishCondition(
                        FallbackSetError(action, value).Expression
                    );
                }

                res = bindingInfo.Body.GetMetaObject(this, value);
            }

            res = new DynamicMetaObject(
                res.Expression,
                self.Restrictions.Merge(res.Restrictions)
            );

            return BindingHelpers.AddDynamicTestAndDefer(
                action,
                res,
                new DynamicMetaObject[] { this, value },
                bindingInfo.Validation
            );
        }

        public override DynamicMetaObject/*!*/ BindDeleteMember(DeleteMemberBinder/*!*/ action) {
            return MakeDeleteMemberRule(
                new DeleteBindingInfo(
                    action,
                    new DynamicMetaObject[] { this },
                    new ConditionalBuilder(action),
                    BindingHelpers.GetValidationInfo(this, PythonType)
                )
            );
        }

        #endregion

        #region Get Member Helpers

        abstract class GetOrInvokeBinderHelper {
            protected readonly MetaUserObject _target;
            protected readonly Expression _codeContext;

            public GetOrInvokeBinderHelper(MetaUserObject target, Expression codeContext) {
                _target = target;
                _codeContext = codeContext;
            }

            public DynamicMetaObject Bind() {
                DynamicMetaObject self = _target.Restrict(Value.GetType());
                CodeContext context = BinderState.GetBinderState(Binder).Context;
                IPythonObject sdo = Value;
                GetBindingInfo bindingInfo = new GetBindingInfo(
                    Binder,
                    new DynamicMetaObject[] { _target },
                    Ast.Variable(Expression.Type, "self"),
                    Ast.Variable(typeof(object), "lookupRes"),
                    new ConditionalBuilder(Binder),
                    BindingHelpers.GetValidationInfo(self, sdo.PythonType)
                );

                PythonTypeSlot foundSlot;
                if (TryGetGetAttribute(context, sdo.PythonType, out foundSlot)) {
                    return Invoke(MakeGetAttributeRule(bindingInfo, sdo, foundSlot, _codeContext));
                }

                // otherwise look the object according to Python rules:
                //  1. 1st search the MRO of the type, and if it's there, and it's a get/set descriptor,
                //      return that value.
                //  2. Look in the instance dictionary.  If it's there return that value, otherwise return
                //      a value found during the MRO search.  If no value was found during the MRO search then
                //      raise an exception.      
                //  3. fall back to __getattr__ if defined.
                //
                // Ultimately we cache the result of the MRO search based upon the type version.  If we have
                // a get/set descriptor we'll only ever use that directly.  Otherwise if we have a get descriptor
                // we'll first check the dictionary and then invoke the get descriptor.  If we have no descriptor
                // at all we'll just check the dictionary.  If both lookups fail we'll raise an exception.

                bool isOldStyle;
                bool systemTypeResolution;
                foundSlot = FindSlot(context, GetGetMemberName(Binder), sdo, out isOldStyle, out systemTypeResolution);

                if (!isOldStyle || foundSlot is ReflectedSlotProperty) {
                    if (sdo.PythonType.HasDictionary && (foundSlot == null || !foundSlot.IsSetDescriptor(context, sdo.PythonType))) {
                        MakeDictionaryAccess(bindingInfo);
                    }

                    if (foundSlot != null) {
                        if (systemTypeResolution) {
                            bindingInfo.Body.FinishCondition(Fallback().Expression);
                        } else {
                            MakeSlotAccess(bindingInfo, foundSlot);
                        }
                    }
                } else {
                    MakeOldStyleAccess(bindingInfo);
                }

                if (!bindingInfo.Body.IsFinal) {
                    // fall back to __getattr__ if it's defined.
                    // TODO: For InvokeMember we should probably do a fallback w/ an error suggestion
                    PythonTypeSlot getattr;
                    if (sdo.PythonType.TryResolveSlot(context, Symbols.GetBoundAttr, out getattr)) {
                        MakeGetAttrRule(bindingInfo, GetWeakSlot(getattr), _codeContext);
                    }

                    bindingInfo.Body.FinishCondition(FallbackError().Expression);
                }

                DynamicMetaObject res = bindingInfo.Body.GetMetaObject(_target);
                res = new DynamicMetaObject(
                    Ast.Block(
                        new ParameterExpression[] { bindingInfo.Self, bindingInfo.Result },
                        Ast.Assign(bindingInfo.Self, self.Expression),
                        res.Expression
                    ),
                    self.Restrictions.Merge(res.Restrictions)
                );

                return BindingHelpers.AddDynamicTestAndDefer(
                    Binder,
                    res,
                    new DynamicMetaObject[] { _target },
                    bindingInfo.Validation
                );
            }

            /// <summary>
            /// Makes a rule which calls a user-defined __getattribute__ function and falls back to __getattr__ if that
            /// raises an AttributeError.
            /// 
            /// slot is the __getattribute__ method to be called.
            /// </summary>
            private DynamicMetaObject/*!*/ MakeGetAttributeRule(GetBindingInfo/*!*/ info, IPythonObject/*!*/ obj, PythonTypeSlot/*!*/ slot, Expression codeContext) {
                // if the type implements IDynamicMetaObjectProvider and we picked up it's __getattribute__ then we want to just 
                // dispatch to the base meta object (or to the default binder). an example of this is:
                //
                // class mc(type):
                //     def __getattr__(self, name):
                //          return 42
                //
                // class nc_ga(object):
                //     __metaclass__ = mc
                //
                // a = nc_ga.x # here we want to dispatch to the type's rule, not call __getattribute__ directly.

                CodeContext context = BinderState.GetBinderState(info.Action).Context;
                Type finalType = PythonTypeOps.GetFinalSystemType(obj.PythonType.UnderlyingSystemType);
                if (typeof(IDynamicMetaObjectProvider).IsAssignableFrom(finalType)) {
                    PythonTypeSlot baseSlot;
                    if (TryGetGetAttribute(context, DynamicHelpers.GetPythonTypeFromType(finalType), out baseSlot) && baseSlot == slot) {
                        return FallbackError();
                    }
                }

                // otherwise generate code into a helper function.  This will do the slot lookup and exception
                // handling for both __getattribute__ as well as __getattr__ if it exists.
                PythonTypeSlot getattr;
                obj.PythonType.TryResolveSlot(context, Symbols.GetBoundAttr, out getattr);
                DynamicMetaObject self = _target.Restrict(Value.GetType());
                string methodName = BindingHelpers.IsNoThrow(info.Action) ? "GetAttributeNoThrow" : "GetAttribute";

                return BindingHelpers.AddDynamicTestAndDefer(
                    info.Action,
                    new DynamicMetaObject(
                        Ast.Call(
                            typeof(UserTypeOps).GetMethod(methodName),
                            Ast.Constant(BinderState.GetBinderState(info.Action).Context),
                            info.Args[0].Expression,
                            Ast.Constant(GetGetMemberName(info.Action)),
                            Ast.Constant(slot, typeof(PythonTypeSlot)),
                            Ast.Constant(getattr, typeof(PythonTypeSlot)),
                            Ast.Constant(new SiteLocalStorage<CallSite<Func<CallSite, CodeContext, object, string, object>>>())
                        ),
                        self.Restrictions
                    ),
                    info.Args,
                    info.Validation
                );
            }

            private void MakeGetAttrRule(GetBindingInfo/*!*/ info, Expression/*!*/ getattr, Expression codeContext) {
                info.Body.AddCondition(
                    MakeGetAttrTestAndGet(info, getattr),
                    Invoke(MakeGetAttrCall(info, codeContext))
                );
            }

            private Expression/*!*/ MakeGetAttrCall(GetBindingInfo/*!*/ info, Expression codeContext) {
                Expression call = Ast.Dynamic(
                    BinderState.GetBinderState(info.Action).InvokeOne,
                    typeof(object),
                    BinderState.GetCodeContext(info.Action),
                    info.Result,
                    Ast.Constant(GetGetMemberName(info.Action))
                );

                call = MaybeMakeNoThrow(info, call, codeContext);

                return call;
            }

            private Expression/*!*/ MaybeMakeNoThrow(GetBindingInfo/*!*/ info, Expression/*!*/ expr, Expression codeContext) {
                if (BindingHelpers.IsNoThrow(info.Action)) {
                    DynamicMetaObject fallback = FallbackError();
                    Type t = BindingHelpers.GetCompatibleType(expr.Type, fallback.Expression.Type);
                    ParameterExpression tmp = Ast.Variable(t, "getAttrRes");

                    expr = Ast.Block(
                        new ParameterExpression[] { tmp },
                        Ast.Block(
                            AstUtils.Try(
                                Ast.Assign(tmp, AstUtils.Convert(expr, t))
                            ).Catch(
                                typeof(MissingMemberException),
                                Ast.Assign(tmp, AstUtils.Convert(FallbackError().Expression, t))
                            ),
                            tmp
                        )
                    );
                }
                return expr;
            }

            private void MakeSlotAccess(GetBindingInfo/*!*/ info, PythonTypeSlot dts) {
                ReflectedSlotProperty rsp = dts as ReflectedSlotProperty;
                if (rsp != null) {
                    // we need to fall back to __getattr__ if the value is not defined, so call it and check the result.
                    info.Body.AddCondition(
                        Ast.NotEqual(
                            Ast.Assign(
                                info.Result,
                                Ast.ArrayAccess(
                                    GetSlots(_target),
                                    Ast.Constant(rsp.Index)
                                )
                            ),
                            Ast.Field(null, typeof(Uninitialized).GetField("Instance"))
                        ),
                        Invoke(info.Result)
                    );
                    return;
                }

                PythonTypeUserDescriptorSlot slot = dts as PythonTypeUserDescriptorSlot;
                if (slot != null && !(slot.Value is PythonTypeSlot)) {
                    PythonType slottype = DynamicHelpers.GetPythonType(slot.Value);
                    if (slottype.IsSystemType) {
                        // this is a user slot that's known not to be a descriptor
                        // so we can just burn the value in.  For it to change the
                        // slot will need to be replaced reving the type version.
                        info.Body.FinishCondition(
                            Invoke(AstUtils.Convert(AstUtils.WeakConstant(slot.Value), typeof(object)))
                        );
                        return;
                    }
                }

                // users can subclass PythonProperty so check the type explicitly 
                // and only in-line the ones we fully understand.
                if (dts.GetType() == typeof(PythonProperty)) {
                    // properties are mutable so we generate code to get the value rather
                    // than burning it into the rule.
                    Expression getter = Ast.Property(
                        Ast.Convert(AstUtils.WeakConstant(dts), typeof(PythonProperty)),
                        "fget"
                    );
                    ParameterExpression tmpGetter = Ast.Variable(typeof(object), "tmpGet");
                    info.Body.AddVariable(tmpGetter);

                    info.Body.FinishCondition(
                        Ast.Block(
                            Ast.Assign(tmpGetter, getter),
                            Ast.Condition(
                                Ast.NotEqual(
                                    tmpGetter,
                                    Ast.Constant(null)
                                ),
                                Invoke(
                                    Ast.Dynamic(
                                        BinderState.GetBinderState(info.Action).InvokeOne,
                                        typeof(object),
                                        Ast.Constant(BinderState.GetBinderState(info.Action).Context),
                                        tmpGetter,
                                        info.Self
                                    )
                                ),
                                Ast.Throw(Ast.Call(typeof(PythonOps).GetMethod("UnreadableProperty")), typeof(object))
                            )
                        )
                    );
                    return;
                }

                Expression tryGet = Ast.Call(
                    TypeInfo._PythonOps.SlotTryGetBoundValue,
                    Ast.Constant(BinderState.GetBinderState(info.Action).Context),
                    Ast.Convert(AstUtils.WeakConstant(dts), typeof(PythonTypeSlot)),
                    AstUtils.Convert(info.Self, typeof(object)),
                    Ast.Property(
                        Ast.Convert(
                            info.Self,
                            typeof(IPythonObject)),
                        TypeInfo._IPythonObject.PythonType
                    ),
                    info.Result
                );

                Expression value = Invoke(info.Result);
                if (dts.GetAlwaysSucceeds) {
                    info.Body.FinishCondition(
                        Ast.Block(tryGet, value)
                    );
                } else {
                    info.Body.AddCondition(
                        tryGet,
                        value
                    );
                }
            }

            private void MakeDictionaryAccess(GetBindingInfo/*!*/ info) {
                FieldInfo fi = _target.LimitType.GetField(NewTypeMaker.DictFieldName);
                Expression dict;
                if (fi != null) {
                    dict = Ast.Field(
                        Ast.Convert(info.Self, _target.LimitType),
                        fi
                    );
                } else {
                    dict = Ast.Property(
                        Ast.Convert(info.Self, typeof(IPythonObject)),
                        TypeInfo._IPythonObject.Dict
                    );
                }
                
                info.Body.AddCondition(
                    Ast.AndAlso(
                        Ast.NotEqual(
                            dict,
                            Ast.Constant(null)
                        ),
                        Ast.Call(
                            dict,
                            TypeInfo._IAttributesCollection.TryGetvalue,
                            AstUtils.Constant(SymbolTable.StringToId(GetGetMemberName(info.Action))),
                            info.Result
                        )
                    ),
                    Invoke(new DynamicMetaObject(info.Result, BindingRestrictions.Empty)).Expression
                );
            }

            /// <summary>
            /// Checks a range of the MRO to perform old-style class lookups if any old-style classes
            /// are present.  We will call this twice to produce a search before a slot and after
            /// a slot.
            /// </summary>
            private void MakeOldStyleAccess(GetBindingInfo/*!*/ info) {
                info.Body.AddCondition(
                    Ast.Call(
                        typeof(UserTypeOps).GetMethod("TryGetMixedNewStyleOldStyleSlot"),
                        Ast.Constant(BinderState.GetBinderState(info.Action).Context),
                        AstUtils.Convert(info.Self, typeof(object)),
                        AstUtils.Constant(SymbolTable.StringToId(GetGetMemberName(info.Action))),
                        info.Result
                    ),
                    Invoke(info.Result)
                );
            }
        
        
            protected abstract DynamicMetaObject FallbackError();

            protected abstract DynamicMetaObject Fallback();
            protected abstract DynamicMetaObjectBinder Binder {
                get;
            }

            protected virtual Expression Invoke(Expression res) {
                return Invoke(new DynamicMetaObject(res, BindingRestrictions.Empty)).Expression;
            }

            protected virtual DynamicMetaObject Invoke(DynamicMetaObject res) {
                return res;
            }

            public IPythonObject Value {
                get {
                    return _target.Value;
                }
            }

            public Expression Expression {
                get {
                    return _target.Expression;
                }
            }
        }

        class GetBinderHelper : GetOrInvokeBinderHelper {
            private readonly DynamicMetaObjectBinder _binder;

            public GetBinderHelper(MetaUserObject target, DynamicMetaObjectBinder binder, Expression codeContext)
                : base(target, codeContext) {
                _binder = binder;
            }

            protected override DynamicMetaObject Fallback() {
                return GetMemberFallback(_target, _binder, _codeContext);
            }

            protected override DynamicMetaObject FallbackError() {
                return _target.FallbackGetError(_binder, _codeContext);
            }

            protected override DynamicMetaObjectBinder Binder {
                get {
                    return _binder;
                }
            }
        }

        class InvokeBinderHelper : GetOrInvokeBinderHelper {
            private readonly InvokeMemberBinder _binder;
            private readonly DynamicMetaObject[] _args;

            public InvokeBinderHelper(MetaUserObject target, InvokeMemberBinder binder, DynamicMetaObject[] args, Expression codeContext)
                : base(target, codeContext) {
                _binder = binder;
                _args = args;
            }

            protected override DynamicMetaObject Fallback() {
                return _binder.FallbackInvokeMember(_target, _args);
            }

            protected override DynamicMetaObject FallbackError() {
                if (_target._baseMetaObject != null) {
                    return _target._baseMetaObject.BindInvokeMember(_binder, _args);
                }

                return Fallback();
            }

            protected override DynamicMetaObject Invoke(DynamicMetaObject res) {
                return _binder.FallbackInvoke(res, _args, null);
            }

            protected override DynamicMetaObjectBinder Binder {
                get {
                    return _binder;
                }
            }
        }

        private DynamicMetaObject GetMemberWorker(DynamicMetaObjectBinder/*!*/ member, Expression codeContext) {
            return new GetBinderHelper(this, member, codeContext).Bind();
        }

        /// <summary>
        /// Checks to see if this type has __getattribute__ that overrides all other attribute lookup.
        /// 
        /// This is more complex then it needs to be.  The problem is that when we have a 
        /// mixed new-style/old-style class we have a weird __getattribute__ defined.  When
        /// we always dispatch through rules instead of PythonTypes it should be easy to remove
        /// this.
        /// </summary>
        private static bool TryGetGetAttribute(CodeContext/*!*/ context, PythonType/*!*/ type, out PythonTypeSlot dts) {
            if (type.TryResolveSlot(context, Symbols.GetAttribute, out dts)) {
                BuiltinMethodDescriptor bmd = dts as BuiltinMethodDescriptor;

                if (bmd == null || bmd.DeclaringType != typeof(object) ||
                    bmd.Template.Targets.Count != 1 ||
                    bmd.Template.Targets[0].DeclaringType != typeof(ObjectOps) ||
                    bmd.Template.Targets[0].Name != "__getattribute__") {
                    return dts != null;
                }
            }
            return false;
        }
        
        private static MethodCallExpression/*!*/ MakeGetAttrTestAndGet(GetBindingInfo/*!*/ info, Expression/*!*/ getattr) {
            return Ast.Call(
                TypeInfo._PythonOps.SlotTryGetBoundValue,
                AstUtils.Constant(BinderState.GetBinderState(info.Action).Context),
                AstUtils.Convert(getattr, typeof(PythonTypeSlot)),
                AstUtils.Convert(info.Self, typeof(object)),
                Ast.Convert(
                    Ast.Property(
                        Ast.Convert(
                            info.Self,
                            typeof(IPythonObject)),
                        TypeInfo._IPythonObject.PythonType
                    ),
                    typeof(PythonType)
                ),
                info.Result
            );
        }               
        
        private static Expression/*!*/ GetWeakSlot(PythonTypeSlot slot) {
            return AstUtils.Convert(AstUtils.WeakConstant(slot), typeof(PythonTypeSlot));
        }

        private static Expression/*!*/ MakeTypeError(string/*!*/ name, PythonType/*!*/ type) {
            return Ast.Throw(
                Ast.Call(
                    typeof(PythonOps).GetMethod("AttributeErrorForMissingAttribute", new Type[] { typeof(string), typeof(SymbolId) }),
                    AstUtils.Constant(type.Name),
                    AstUtils.Constant(SymbolTable.StringToId(name))
                )
            );
        }

        #endregion

        #region Set Member Helpers

        private static bool IsStandardObjectMethod(PythonTypeSlot dts) {
            BuiltinMethodDescriptor bmd = dts as BuiltinMethodDescriptor;
            if (bmd == null) return false;
            return bmd.Template.Targets[0].DeclaringType == typeof(ObjectOps);
        }

        private void MakeSetAttrTarget(SetBindingInfo bindingInfo, IPythonObject sdo, PythonTypeSlot dts) {
            ParameterExpression tmp = Ast.Variable(typeof(object), "boundVal");
            bindingInfo.Body.AddVariable(tmp);

            bindingInfo.Body.AddCondition(
                Ast.Call(
                    typeof(PythonOps).GetMethod("SlotTryGetValue"),
                    AstUtils.Constant(BinderState.GetBinderState(bindingInfo.Action).Context),
                    AstUtils.Convert(AstUtils.WeakConstant(dts), typeof(PythonTypeSlot)),
                    AstUtils.Convert(bindingInfo.Args[0].Expression, typeof(object)),
                    AstUtils.Convert(AstUtils.WeakConstant(sdo.PythonType), typeof(PythonType)),
                    tmp
                ),
                Ast.Dynamic(
                    BinderState.GetBinderState(bindingInfo.Action).Invoke(
                        new CallSignature(2)
                    ),
                    typeof(object),
                    BinderState.GetCodeContext(bindingInfo.Action),
                    tmp,
                    AstUtils.Constant(bindingInfo.Action.Name),
                    bindingInfo.Args[1].Expression
                )
            );

            bindingInfo.Body.FinishCondition(
                FallbackSetError(bindingInfo.Action, bindingInfo.Args[1]).Expression
            );
        }

        private static void MakeSlotsDeleteTarget(MemberBindingInfo/*!*/ info, ReflectedSlotProperty/*!*/ rsp) {
            MakeSlotsSetTarget(info, rsp, Ast.Field(null, typeof(Uninitialized).GetField("Instance")));
        }

        private static void MakeSlotsSetTarget(MemberBindingInfo/*!*/ info, ReflectedSlotProperty/*!*/ rsp, Expression/*!*/ value) {
            // type has __slots__ defined for this member, call the setter directly
            ParameterExpression tmp = Ast.Variable(typeof(object), "res");
            info.Body.AddVariable(tmp);

            info.Body.FinishCondition(
                Ast.Block(
                    Ast.Assign(
                        tmp,
                        Ast.Convert(
                            Ast.Assign(
                                Ast.ArrayAccess(
                                    GetSlots(info.Args[0]),
                                    AstUtils.Constant(rsp.Index)
                                ),
                                AstUtils.Convert(value, typeof(object))
                            ),
                            tmp.Type
                        )
                    ),
                    tmp
                )
            );
        }


        private static DynamicMetaObject MakeSlotSet(SetBindingInfo/*!*/ info, PythonTypeSlot/*!*/ dts) {
            ParameterExpression tmp = Ast.Variable(info.Args[1].Expression.Type, "res");
            info.Body.AddVariable(tmp);

            // users can subclass PythonProperty so check the type explicitly 
            // and only in-line the ones we fully understand.
            if (dts.GetType() == typeof(PythonProperty)) {
                // properties are mutable so we generate code to get the value rather
                // than burning it into the rule.
                Expression setter = Ast.Property(
                    Ast.Convert(AstUtils.WeakConstant(dts), typeof(PythonProperty)),
                    "fset"
                );
                ParameterExpression tmpSetter = Ast.Variable(typeof(object), "tmpSet");
                info.Body.AddVariable(tmpSetter);

                info.Body.FinishCondition(
                    Ast.Block(
                        Ast.Assign(tmpSetter, setter),
                        Ast.Condition(
                            Ast.NotEqual(
                                tmpSetter,
                                AstUtils.Constant(null)
                            ),
                            Ast.Block(
                                Ast.Assign(tmp, info.Args[1].Expression),
                                Ast.Dynamic(
                                    BinderState.GetBinderState(info.Action).InvokeOne,
                                    typeof(void),
                                    AstUtils.Constant(BinderState.GetBinderState(info.Action).Context),
                                    tmpSetter,
                                    info.Args[0].Expression,
                                    AstUtils.Convert(tmp, typeof(object))
                                ),
                                tmp
                            ),
                            Ast.Throw(Ast.Call(typeof(PythonOps).GetMethod("UnsetableProperty")), tmp.Type)
                        )
                    )
                );
                return info.Body.GetMetaObject();
            }

            CodeContext context = BinderState.GetBinderState(info.Action).Context;
            Debug.Assert(context != null);

            info.Body.AddCondition(
                Ast.Block(
                    Ast.Assign(tmp, info.Args[1].Expression),
                    Ast.Call(
                        typeof(PythonOps).GetMethod("SlotTrySetValue"),
                        AstUtils.Constant(context),
                        AstUtils.Convert(AstUtils.WeakConstant(dts), typeof(PythonTypeSlot)),
                        AstUtils.Convert(info.Args[0].Expression, typeof(object)),
                        Ast.Convert(
                            Ast.Property(
                                Ast.Convert(
                                    info.Args[0].Expression,
                                    typeof(IPythonObject)),
                                TypeInfo._IPythonObject.PythonType
                            ),
                            typeof(PythonType)
                        ),
                        AstUtils.Convert(tmp, typeof(object))
                    )
                ),
                tmp
            );
            return null;
        }

        private static void MakeDictionarySetTarget(SetBindingInfo/*!*/ info) {
            FieldInfo fi = info.Args[0].LimitType.GetField(NewTypeMaker.DictFieldName);
            if (fi != null) {
                // return UserTypeOps.FastSetDictionaryValue(ref this._dict, name, value);
                info.Body.FinishCondition(
                    Ast.Call(
                        typeof(UserTypeOps).GetMethod("FastSetDictionaryValue"),
                        Ast.Field(
                            Ast.Convert(info.Args[0].Expression, info.Args[0].LimitType), 
                            fi
                        ),
                        AstUtils.Constant(SymbolTable.StringToId(info.Action.Name)),
                        AstUtils.Convert(info.Args[1].Expression, typeof(object))
                    )
                );
            } else {
                // return UserTypeOps.SetDictionaryValue(rule.Parameters[0], name, value);
                info.Body.FinishCondition(
                    Ast.Call(
                        typeof(UserTypeOps).GetMethod("SetDictionaryValue"),
                        Ast.Convert(info.Args[0].Expression, typeof(IPythonObject)),
                        AstUtils.Constant(SymbolTable.StringToId(info.Action.Name)),
                        AstUtils.Convert(info.Args[1].Expression, typeof(object))
                    )
                );
            }
        }

        #endregion

        #region Delete Member Helpers

        private DynamicMetaObject/*!*/ MakeDeleteMemberRule(DeleteBindingInfo/*!*/ info) {
            CodeContext context = BinderState.GetBinderState(info.Action).Context;
            DynamicMetaObject self = info.Args[0].Restrict(info.Args[0].GetRuntimeType());

            IPythonObject sdo = info.Args[0].Value as IPythonObject;
            if (info.Action.Name == "__class__") {
                return new DynamicMetaObject(
                    Ast.Throw(
                        Ast.New(
                            typeof(ArgumentTypeException).GetConstructor(new Type[] { typeof(string) }),
                            AstUtils.Constant("can't delete __class__ attribute")
                        )
                    ),
                    self.Restrictions
                );
            }

            // call __delattr__ if it exists
            PythonTypeSlot dts;
            if (sdo.PythonType.TryResolveSlot(context, Symbols.DelAttr, out dts) && !IsStandardObjectMethod(dts)) {
                MakeDeleteAttrTarget(info, sdo, dts);
            }

            // then see if we have a delete descriptor
            sdo.PythonType.TryResolveSlot(context, SymbolTable.StringToId(info.Action.Name), out dts);
            ReflectedSlotProperty rsp = dts as ReflectedSlotProperty;
            if (rsp != null) {
                MakeSlotsDeleteTarget(info, rsp);
            }
            
            if (!info.Body.IsFinal && dts != null) {
                MakeSlotDelete(info, dts);
            }

            if (!info.Body.IsFinal && sdo.PythonType.HasDictionary) {
                // finally if we have a dictionary set the value there.
                MakeDictionaryDeleteTarget(info);
            }

            if (!info.Body.IsFinal) {
                // otherwise fallback
                info.Body.FinishCondition(
                    FallbackDeleteError(info.Action, info.Args).Expression
                );
            }

            DynamicMetaObject res = info.Body.GetMetaObject(info.Args);

            res = new DynamicMetaObject(
                res.Expression,
                self.Restrictions.Merge(res.Restrictions)
            );

            return BindingHelpers.AddDynamicTestAndDefer(
                info.Action,
                res,
                info.Args,
                info.Validation
            );

        }

        private static DynamicMetaObject MakeSlotDelete(DeleteBindingInfo/*!*/ info, PythonTypeSlot/*!*/ dts) {

            // users can subclass PythonProperty so check the type explicitly 
            // and only in-line the ones we fully understand.
            if (dts.GetType() == typeof(PythonProperty)) {
                // properties are mutable so we generate code to get the value rather
                // than burning it into the rule.
                Expression deleter = Ast.Property(
                    Ast.Convert(AstUtils.WeakConstant(dts), typeof(PythonProperty)),
                    "fdel"
                );
                ParameterExpression tmpDeleter = Ast.Variable(typeof(object), "tmpDel");
                info.Body.AddVariable(tmpDeleter);

                info.Body.FinishCondition(
                    Ast.Block(
                        Ast.Assign(tmpDeleter, deleter),
                        Ast.Condition(
                            Ast.NotEqual(
                                tmpDeleter,
                                AstUtils.Constant(null)
                            ),
                            Ast.Dynamic(
                                BinderState.GetBinderState(info.Action).InvokeOne,
                                typeof(void),
                                AstUtils.Constant(BinderState.GetBinderState(info.Action).Context),
                                tmpDeleter,
                                info.Args[0].Expression
                            ),
                            Ast.Throw(Ast.Call(typeof(PythonOps).GetMethod("UndeletableProperty")))
                        )
                    )
                );
                return info.Body.GetMetaObject();
            }

            info.Body.AddCondition(
                Ast.Call(
                    typeof(PythonOps).GetMethod("SlotTryDeleteValue"),
                    AstUtils.Constant(BinderState.GetBinderState(info.Action).Context),
                    AstUtils.Convert(AstUtils.WeakConstant(dts), typeof(PythonTypeSlot)),
                    AstUtils.Convert(info.Args[0].Expression, typeof(object)),
                    Ast.Convert(
                        Ast.Property(
                            Ast.Convert(
                                info.Args[0].Expression,
                                typeof(IPythonObject)),
                            TypeInfo._IPythonObject.PythonType
                        ),
                        typeof(PythonType)
                    )
                ),
                AstUtils.Constant(null)
            );
            return null;
        }

        private static void MakeDeleteAttrTarget(DeleteBindingInfo/*!*/ info, IPythonObject self, PythonTypeSlot dts) {
            ParameterExpression tmp = Ast.Variable(typeof(object), "boundVal");
            info.Body.AddVariable(tmp);

            // call __delattr__
            info.Body.AddCondition(
                Ast.Call(
                    TypeInfo._PythonOps.SlotTryGetBoundValue,
                    AstUtils.Constant(BinderState.GetBinderState(info.Action).Context),
                    AstUtils.Convert(AstUtils.WeakConstant(dts), typeof(PythonTypeSlot)),
                    AstUtils.Convert(info.Args[0].Expression, typeof(object)),
                    AstUtils.Convert(AstUtils.WeakConstant(self.PythonType), typeof(PythonType)),
                    tmp
                ),
                Ast.Dynamic(
                    BinderState.GetBinderState(info.Action).InvokeOne,
                    typeof(object),
                    BinderState.GetCodeContext(info.Action),
                    tmp,
                    AstUtils.Constant(info.Action.Name)
                )
            );
        }

        private static void MakeDictionaryDeleteTarget(DeleteBindingInfo/*!*/ info) {
            info.Body.FinishCondition(
                Ast.Call(
                    typeof(UserTypeOps).GetMethod("RemoveDictionaryValue"),
                    Ast.Convert(info.Args[0].Expression, typeof(IPythonObject)),
                    AstUtils.Constant(SymbolTable.StringToId(info.Action.Name))
                )
            );
        }

        #endregion

        #region Common Helpers

        /// <summary>
        /// Looks up the associated PythonTypeSlot from the object.  Indicates if the result
        /// came from a standard .NET type in which case we will fallback to the sites binder.
        /// </summary>
        private static PythonTypeSlot FindSlot(CodeContext/*!*/ context, string/*!*/ name, IPythonObject/*!*/ sdo, out bool isOldStyle, out bool systemTypeResolution) {
            PythonTypeSlot foundSlot = null;
            isOldStyle = false;                // if we're mixed new-style/old-style we have to do a slower check
            systemTypeResolution = false;      // if we pick up the property from a System type we fallback

            SymbolId lookingFor = SymbolTable.StringToId(name);

            foreach (PythonType pt in sdo.PythonType.ResolutionOrder) {
                if (pt.IsOldClass) {
                    isOldStyle = true;
                }

                if (pt.TryLookupSlot(context, lookingFor, out foundSlot)) {
                    // use our built-in binding for ClassMethodDescriptors rather than falling back
                    if (!(foundSlot is ClassMethodDescriptor)) {
                        systemTypeResolution = pt.IsSystemType;
                    }
                    break;
                }
            }

            return foundSlot;
        }

        #endregion

        #region BindingInfo classes

        class MemberBindingInfo {
            public readonly ConditionalBuilder/*!*/ Body;
            public readonly DynamicMetaObject/*!*/[]/*!*/ Args;
            public readonly ValidationInfo/*!*/ Validation;

            public MemberBindingInfo(DynamicMetaObject/*!*/[]/*!*/ args, ConditionalBuilder/*!*/ body, ValidationInfo/*!*/ validation) {
                Body = body;
                Validation = validation;
                Args = args;
            }
        }

        class DeleteBindingInfo : MemberBindingInfo {
            public readonly DeleteMemberBinder/*!*/ Action;

            public DeleteBindingInfo(DeleteMemberBinder/*!*/ action, DynamicMetaObject/*!*/[]/*!*/ args, ConditionalBuilder/*!*/ body, ValidationInfo/*!*/ validation)
                : base(args, body, validation) {
                Action = action;
            }
        }

        class SetBindingInfo : MemberBindingInfo {
            public readonly SetMemberBinder/*!*/ Action;

            public SetBindingInfo(SetMemberBinder/*!*/ action, DynamicMetaObject/*!*/[]/*!*/ args, ConditionalBuilder/*!*/ body, ValidationInfo/*!*/ validation)
                : base(args, body, validation) {
                Action = action;
            }
        }

        class GetBindingInfo : MemberBindingInfo {
            public readonly DynamicMetaObjectBinder/*!*/ Action;
            public readonly ParameterExpression/*!*/ Self, Result;

            public GetBindingInfo(DynamicMetaObjectBinder/*!*/ action, DynamicMetaObject/*!*/[]/*!*/ args, ParameterExpression/*!*/ self, ParameterExpression/*!*/ result, ConditionalBuilder/*!*/ body, ValidationInfo/*!*/ validationInfo)
                : base(args, body, validationInfo) {
                Action = action;
                Self = self;
                Result = result;
            }
        }

        #endregion

        #region Fallback Helpers

        /// <summary>
        /// Helper for falling back - if we have a base object fallback to it first (which can
        /// then fallback to the calling site), otherwise fallback to the calling site.
        /// </summary>
        private DynamicMetaObject/*!*/ FallbackGetError(DynamicMetaObjectBinder/*!*/ action, Expression codeContext) {
            if (_baseMetaObject != null) {
                return Fallback(action, codeContext);
            } else if (BindingHelpers.IsNoThrow(action)) {
                return new DynamicMetaObject(
                    Ast.Field(null, typeof(OperationFailed).GetField("Value")),
                    BindingRestrictions.Empty
                );
            } else if (action is PythonGetMemberBinder) {
                return new DynamicMetaObject(
                    MakeTypeError(GetGetMemberName(action), PythonType),
                    BindingRestrictions.Empty
                );
            }

            return GetMemberFallback(this, action, codeContext);
        }

        /// <summary>
        /// Helper for falling back - if we have a base object fallback to it first (which can
        /// then fallback to the calling site), otherwise fallback to the calling site.
        /// </summary>
        private DynamicMetaObject/*!*/ FallbackSetError(SetMemberBinder/*!*/ action, DynamicMetaObject/*!*/ value) {
            if (_baseMetaObject != null) {
                return _baseMetaObject.BindSetMember(action, value);
            } else if (action is PythonSetMemberBinder) {
                return new DynamicMetaObject(
                    MakeTypeError(action.Name, Value.PythonType),
                    BindingRestrictions.Empty
                );
            }

            return action.FallbackSetMember(this, value);
        }


        /// <summary>
        /// Helper for falling back - if we have a base object fallback to it first (which can
        /// then fallback to the calling site), otherwise fallback to the calling site.
        /// </summary>
        private DynamicMetaObject/*!*/ FallbackDeleteError(DeleteMemberBinder/*!*/ action, DynamicMetaObject/*!*/[] args) {
            if (_baseMetaObject != null) {
                return _baseMetaObject.BindDeleteMember(action);
            } else if (action is PythonDeleteMemberBinder) {
                return new DynamicMetaObject(
                    MakeTypeError(action.Name, ((IPythonObject)args[0].Value).PythonType),
                    BindingRestrictions.Empty
                );
            }

            return action.FallbackDeleteMember(this);
        }

        #endregion

        private static Expression/*!*/ GetSlots(DynamicMetaObject/*!*/ self) {
            FieldInfo fi = self.LimitType.GetField(NewTypeMaker.SlotsAndWeakRefFieldName);
            if (fi != null) {
                return Ast.Field(
                    Ast.Convert(self.Expression, self.LimitType),
                    fi
                );
            }
            return Ast.Call(
                Ast.Convert(self.Expression, typeof(IPythonObject)),
                typeof(IPythonObject).GetMethod("GetSlots")
            );
        }

    }
}
