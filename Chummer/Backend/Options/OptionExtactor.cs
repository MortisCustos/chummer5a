﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Chummer.Datastructures;
using Chummer.Backend.Attributes.OptionAttributes;
using Chummer.Classes;
using Mono.CodeGeneration;

namespace Chummer.Backend.Options
{
    public class OptionExtactor
    {
        private readonly List<Predicate<PropertyInfo>> _supported;

        public OptionExtactor(List<Predicate<PropertyInfo>> supported)
        {
            //Make copy. I don't event want to think about what happens if somebody changes it while running.
            _supported = new List<Predicate<PropertyInfo>>(supported);
        }

        public SimpleTree<OptionEntryProxy> Extract(object target)
        {
            SimpleTree<OptionEntryProxy> root = new SimpleTree<OptionEntryProxy> {Tag = "root"};

            DictionaryList<string, PropertyInfo> propertiesDisplayPath = new DictionaryList<string, PropertyInfo>();
            Dictionary<string, OptionEntryProxy> proxies = new Dictionary<string, OptionEntryProxy>();
            Dictionary<PropertyInfo, OptionConstaint> constaints =
                new Dictionary<PropertyInfo, OptionConstaint>();

            string currentName = "";
            SimpleTree<OptionEntryProxy> parentTree;
            string[] npath;

            OptionConstaint currentConstaint = null;

            //BAD JOHANNES: what did we say about logic in forms?
            //to be fair, rest of code is winform specific too

            //Collect all properties in groups based on their option path
            foreach (PropertyInfo info in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (typeof(OptionConstaint).IsAssignableFrom(info.PropertyType))
                {
                    if(currentConstaint != null) throw new ArgumentException("Multiple constaints on one property detected.");
                    currentConstaint = (OptionConstaint) info.GetValue(target);
                }

                if(!info.GetMethod.IsPublic) continue;


                if (info.GetCustomAttribute<DisplayIgnoreAttribute>() != null) continue;
                if (info.GetCustomAttribute<OptionAttributes>() != null)
                {
                    currentName = info.GetCustomAttribute<OptionAttributes>().Path;
                }

                propertiesDisplayPath.Add(currentName, info);

                if (currentConstaint != null)
                {
                    constaints.Add(info, currentConstaint);
                    currentConstaint = null;
                }
            }


            var temp = propertiesDisplayPath
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .OrderBy(x => x.Key);

            foreach (KeyValuePair<string, List<PropertyInfo>> group in temp)
            {
                string[] path = group.Key.Split('/');
                SimpleTree<OptionEntryProxy> parrent = root;

                //find path in option tree, skip last as thats new
                //Breaks if trying to "jump" a path element
                for (int i = 0; i < path.Length - 1; i++)
                {
                    parrent = parrent.Children.First(x => (string) x.Tag == path[i]);
                }

                SimpleTree<OptionEntryProxy> newChild = new SimpleTree<OptionEntryProxy> {Tag = path.Last()};


                foreach (OptionEntryProxy entryProxy in group.Value
                    .Where(p => _supported.Any(x => x(p)))
                    .Select(p => CreateOptionEntry(target, p))
                    .Where(x => x != null))
                {
                    newChild.Leafs.Add(entryProxy);
                    proxies.Add(entryProxy.TargetProperty.Name, entryProxy);
                }
                parrent.Children.Add(newChild);
            }

            SetupConstraints(constaints, proxies);

            return root;
        }

        private void SetupConstraints(Dictionary<PropertyInfo, OptionConstaint> constaints, Dictionary<string, OptionEntryProxy> proxies)
        {
            foreach (KeyValuePair<PropertyInfo, OptionConstaint> constaint in constaints)
            {
                LambdaExpression ex = constaint.Value.Ex;
                PropertyExtractorExpressionVisitor visitor = new PropertyExtractorExpressionVisitor(constaint.Key.DeclaringType);
                LambdaExpression fix = visitor.Process(ex);
                OptionEntryProxy target = proxies[constaint.Key.Name];
                target.SetConstaint(
                    (Func<List<OptionEntryProxy>, bool>) fix.Compile(),
                    visitor.FoundProperties.Select(x => proxies[x]).ToList());

            }
        }

        private OptionEntryProxy CreateOptionEntry(object target, PropertyInfo arg)
        {
            try
            {
                DisplayConfigurationAttribute disp = arg.GetCustomAttribute<DisplayConfigurationAttribute>();
                string displayString;
                string toolTip = null;
                if (disp != null)
                {
                    displayString = LanguageManager.Instance.GetString(disp.DisplayName);
                    if (!string.IsNullOrWhiteSpace(disp.Tooltip))
                    {
                        toolTip = LanguageManager.Instance.GetString(disp.Tooltip);
                    }
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (char c in arg.Name)
                    {
                        if (c == '_')
                            sb.Append(' ');
                        else if (char.IsUpper(c))
                        {
                            sb.Append(' ');
                            sb.Append(c);
                        }
                        else sb.Append(c);
                    }
                    displayString = sb.ToString();
                }

                return new OptionEntryProxy(target, arg, displayString, toolTip);
            }
            catch (Exception ex)
            {
                Log.Error(new object[]{ex, arg});

                if (Debugger.IsAttached)
                    throw;
                else return null;
            }
        }

        private class PropertyExtractorExpressionVisitor : ExpressionVisitor
        {
            //This would give bugs if trying to access parameters from another class of same type.

            private readonly Type _delclaringType;

            public PropertyExtractorExpressionVisitor(Type delclaringType)
            {
                _delclaringType = delclaringType;
            }

            public List<string> FoundProperties { get; } = new List<string>();
            private ParameterExpression param;

            public LambdaExpression Process(LambdaExpression expression)
            {
                param = Expression.Parameter(typeof(List<OptionEntryProxy>), expression.Parameters[0].Name);

                var bod = Visit(expression.Body);

                return Expression.Lambda<Func<List<OptionEntryProxy>, bool>>(bod, param);
            }

            public override Expression Visit(Expression node)
            {
                if (node.NodeType == ExpressionType.MemberAccess)
                {
                    MemberExpression ex = (MemberExpression) node;
                    PropertyInfo info = ex.Member as PropertyInfo;
                    if (info != null)
                    {
                        if (info.DeclaringType == _delclaringType)
                        {
                            FoundProperties.Add(info.Name);
                            return Expression.Convert(
                                Expression.Property(
                                    Expression.Property(
                                        param, "Item",
                                        Expression.Constant(FoundProperties.Count - 1)),
                                    "Value"
                                ),
                                info.PropertyType
                            );

                        }

                    }

                }
                return base.Visit(node);
            }
        }
    }
}