using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Internal;
using static Npgsql.EntityFrameworkCore.PostgreSQL.Utilities.Statics;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Query.ExpressionTranslators.Internal
{
    public class NpgsqlLTreeTranslator : IMethodCallTranslator, IMemberTranslator
    {
        readonly IRelationalTypeMappingSource _typeMappingSource;
        readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;
        readonly RelationalTypeMapping _boolTypeMapping;
        readonly RelationalTypeMapping _ltreeTypeMapping;
        readonly RelationalTypeMapping _ltreeArrayTypeMapping;
        readonly RelationalTypeMapping _lqueryTypeMapping;
        readonly RelationalTypeMapping _lqueryArrayTypeMapping;
        readonly RelationalTypeMapping _ltxtqueryTypeMapping;

        static readonly MethodInfo IsAncestorOf =
            typeof(LTree).GetRuntimeMethod(nameof(LTree.IsAncestorOf), new[] { typeof(LTree) });

        static readonly MethodInfo IsDescendantOf =
            typeof(LTree).GetRuntimeMethod(nameof(LTree.IsDescendantOf), new[] { typeof(LTree) });

        static readonly MethodInfo MatchesLQuery =
            typeof(LTree).GetRuntimeMethod(nameof(LTree.MatchesLQuery), new[] { typeof(string) });

        static readonly MethodInfo MatchesLTxtQuery =
            typeof(LTree).GetRuntimeMethod(nameof(LTree.MatchesLTxtQuery), new[] { typeof(string) });

        public NpgsqlLTreeTranslator(
            [NotNull] IRelationalTypeMappingSource typeMappingSource,
            [NotNull] NpgsqlSqlExpressionFactory sqlExpressionFactory)
        {
            _typeMappingSource = typeMappingSource;
            _sqlExpressionFactory = sqlExpressionFactory;
            _boolTypeMapping = typeMappingSource.FindMapping(typeof(bool));
            _ltreeTypeMapping = typeMappingSource.FindMapping(typeof(LTree));
            _ltreeArrayTypeMapping = typeMappingSource.FindMapping(typeof(LTree[]));
            _lqueryTypeMapping = typeMappingSource.FindMapping("lquery");
            _lqueryArrayTypeMapping = typeMappingSource.FindMapping("lquery[]");
            _ltxtqueryTypeMapping = typeMappingSource.FindMapping("ltxtquery");
        }

        /// <inheritdoc />
        public virtual SqlExpression Translate(
            SqlExpression instance,
            MethodInfo method,
            IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (method.DeclaringType == typeof(LTree))
            {
                return method.Name switch
                {
                    nameof(LTree.IsAncestorOf)
                        => new PostgresBinaryExpression(
                            PostgresExpressionType.Contains,
                            _sqlExpressionFactory.ApplyTypeMapping(instance, _ltreeTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(arguments[0], _ltreeTypeMapping),
                            typeof(bool),
                            _boolTypeMapping),

                    nameof(LTree.IsDescendantOf)
                        => new PostgresBinaryExpression(
                            PostgresExpressionType.ContainedBy,
                            _sqlExpressionFactory.ApplyTypeMapping(instance, _ltreeTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(arguments[0], _ltreeTypeMapping),
                            typeof(bool),
                            _boolTypeMapping),

                    nameof(LTree.MatchesLQuery)
                        => new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeMatches,
                            _sqlExpressionFactory.ApplyTypeMapping(instance, _ltreeTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(arguments[0], _lqueryTypeMapping),
                            typeof(bool),
                            _boolTypeMapping),

                    nameof(LTree.MatchesLTxtQuery)
                        => new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeMatches,
                            _sqlExpressionFactory.ApplyTypeMapping(instance, _ltreeTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(arguments[0], _ltxtqueryTypeMapping),
                            typeof(bool),
                            _boolTypeMapping),

                     nameof(LTree.Subtree)
                         => _sqlExpressionFactory.Function(
                             "subltree",
                             new[] { instance, arguments[0], arguments[1] },
                             nullable: true,
                             TrueArrays[3],
                             typeof(LTree),
                             _ltreeTypeMapping),

                     nameof(LTree.Subpath)
                         => _sqlExpressionFactory.Function(
                             "subpath",
                             arguments.Count == 2
                                 ? new[] { instance, arguments[0], arguments[1] }
                                 : new[] { instance, arguments[0] },
                             nullable: true,
                             arguments.Count == 2 ? TrueArrays[3] : TrueArrays[2],
                             typeof(LTree),
                             _ltreeTypeMapping),

                     nameof(LTree.Index)
                         => _sqlExpressionFactory.Function(
                             "index",
                             arguments.Count == 2
                                 ? new[] { instance, arguments[0], arguments[1] }
                                 : new[] { instance, arguments[0] },
                             nullable: true,
                             arguments.Count == 2 ? TrueArrays[3] : TrueArrays[2],
                             typeof(int)),

                    nameof(LTree.LongestCommonAncestor)
                        => _sqlExpressionFactory.Function(
                            "lca",
                            new[] { arguments[0] },
                            nullable: true,
                            TrueArrays[1],
                            typeof(LTree),
                            _ltreeTypeMapping),

                    _ => null
                };
            }

            return null;
        }

        public virtual SqlExpression Translate(
            SqlExpression instance,
            MemberInfo member,
            Type returnType,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
            => member.DeclaringType == typeof(LTree) && member.Name == nameof(LTree.NLevel)
                ? _sqlExpressionFactory.Function(
                    "nlevel",
                    new[] { instance },
                    nullable: true,
                    TrueArrays[1],
                    typeof(int))
                : null;

        /// <summary>
        /// Called directly from <see cref="NpgsqlSqlTranslatingExpressionVisitor"/> to translate LTree array-related constructs which
        /// cannot be translated in regular method translators, since they require accessing lambdas.
        /// </summary>
        public virtual Expression VisitArrayMethodCall(
            [NotNull] NpgsqlSqlTranslatingExpressionVisitor sqlTranslatingExpressionVisitor,
            [NotNull] MethodInfo method,
            [NotNull] ReadOnlyCollection<Expression> arguments)
        {
            var array = arguments[0];

            {
                if (method.IsClosedFormOf(EnumerableMethods.AnyWithPredicate) &&
                    arguments[1] is LambdaExpression wherePredicate &&
                    wherePredicate.Body is MethodCallExpression wherePredicateMethodCall)
                {
                    var predicateMethod = wherePredicateMethodCall.Method;
                    var predicateInstance = wherePredicateMethodCall.Object;
                    var predicateArguments = wherePredicateMethodCall.Arguments;

                    // Pattern match: new[] { "q1", "q2" }.Any(q => e.SomeLTree.MatchesLQuery(q))
                    // Translation: s.SomeLTree ? ARRAY['q1','q2']
                    if (predicateMethod == MatchesLQuery && predicateArguments[0] == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeMatchesAny,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateInstance), _ltreeTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _lqueryArrayTypeMapping),
                            typeof(bool),
                            _typeMappingSource.FindMapping(typeof(bool)));
                    }

                    // Pattern match: new[] { "t1", "t2" }.Any(t => t.IsAncestorOf(e.SomeLTree))
                    // Translation: ARRAY['t1','t2'] @> s.SomeLTree
                    if (predicateMethod == IsAncestorOf && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.Contains,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _ltreeTypeMapping),
                            typeof(bool),
                            _typeMappingSource.FindMapping(typeof(bool)));
                    }

                    // Pattern match: new[] { "t1", "t2" }.Any(t => t.IsDescendantOf(e.SomeLTree))
                    // Translation: s.SomeLTree <@ ARRAY['t1','t2']
                    if (predicateMethod == IsDescendantOf && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.ContainedBy,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _ltreeTypeMapping),
                            typeof(bool),
                            _typeMappingSource.FindMapping(typeof(bool)));
                    }

                    // Pattern match: new[] { "t1", "t2" }.Any(t => t.MatchesLQuery(lquery))
                    // Translation: ARRAY['t1','t2'] ~ lquery
                    if (predicateMethod == MatchesLQuery && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeMatches,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _lqueryTypeMapping),
                            typeof(bool),
                            _typeMappingSource.FindMapping(typeof(bool)));
                    }

                    // Pattern match: new[] { "t1", "t2" }.Any(t => t.MatchesLTxtQuery(ltxtquery))
                    // Translation: ARRAY['t1','t2'] @ ltxtquery
                    if (predicateMethod == MatchesLTxtQuery && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeMatches,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _ltxtqueryTypeMapping),
                            typeof(bool),
                            _typeMappingSource.FindMapping(typeof(bool)));
                    }

                    // Any within Any (i.e. intersection)
                    if (predicateMethod.IsClosedFormOf(EnumerableMethods.AnyWithPredicate) &&
                        predicateArguments[1] is LambdaExpression nestedWherePredicate &&
                        nestedWherePredicate.Body is MethodCallExpression nestedWherePredicateMethodCall)
                    {
                        var nestedPredicateMethod = nestedWherePredicateMethodCall.Method;
                        var nestedPredicateInstance = nestedWherePredicateMethodCall.Object;
                        var nestedPredicateArguments = nestedWherePredicateMethodCall.Arguments;

                        // Pattern match: new[] { "t1", "t2" }.Any(t => lqueries.Any(q => t.MatchesLQuery(q)))
                        // Translation: ARRAY['t1','t2'] ~ ARRAY['q1', 'q2']
                        if (nestedPredicateMethod == MatchesLQuery &&
                            nestedPredicateInstance == wherePredicate.Parameters[0] &&
                            nestedPredicateArguments[0] == nestedWherePredicate.Parameters[0])
                        {
                            return new PostgresBinaryExpression(
                                PostgresExpressionType.LTreeMatchesAny,
                                _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                                _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _lqueryArrayTypeMapping),
                                typeof(bool),
                                _typeMappingSource.FindMapping(typeof(bool)));
                        }
                    }
                }
            }

            {
                if (method.IsClosedFormOf(EnumerableMethods.FirstOrDefaultWithPredicate) &&
                    arguments[1] is LambdaExpression wherePredicate &&
                    wherePredicate.Body is MethodCallExpression wherePredicateMethodCall)
                {
                    var predicateMethod = wherePredicateMethodCall.Method;
                    var predicateInstance = wherePredicateMethodCall.Object;
                    var predicateArguments = wherePredicateMethodCall.Arguments;

                    // Pattern match: new[] { "t1", "t2" }.FirstOrDefault(t => t.IsAncestorOf(e.SomeLTree))
                    // Translation: ARRAY['t1','t2'] ?@> e.SomeLTree
                    if (predicateMethod == IsAncestorOf && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeFirstAncestor,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _ltreeTypeMapping),
                            typeof(LTree),
                            _ltreeTypeMapping);
                    }

                    // Pattern match: new[] { "t1", "t2" }.FirstOrDefault(t => t.IsDescendant(e.SomeLTree))
                    // Translation: ARRAY['t1','t2'] ?<@ e.SomeLTree
                    if (predicateMethod == IsDescendantOf && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeFirstDescendent,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _ltreeTypeMapping),
                            typeof(LTree),
                            _ltreeTypeMapping);
                    }

                    // Pattern match: new[] { "t1", "t2" }.FirstOrDefault(t => t.MatchesLQuery(lquery))
                    // Translation: ARRAY['t1','t2'] ?~ e.lquery
                    if (predicateMethod == MatchesLQuery && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeFirstMatches,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _lqueryTypeMapping),
                            typeof(LTree),
                            _ltreeTypeMapping);
                    }

                    // Pattern match: new[] { "t1", "t2" }.FirstOrDefault(t => t.MatchesLQuery(ltxtquery))
                    // Translation: ARRAY['t1','t2'] ?@ e.ltxtquery
                    if (predicateMethod == MatchesLTxtQuery && predicateInstance == wherePredicate.Parameters[0])
                    {
                        return new PostgresBinaryExpression(
                            PostgresExpressionType.LTreeFirstMatches,
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(array), _ltreeArrayTypeMapping),
                            _sqlExpressionFactory.ApplyTypeMapping(Visit(predicateArguments[0]), _ltxtqueryTypeMapping),
                            typeof(string),
                            _ltreeTypeMapping);
                    }
                }
            }

            return null;

            SqlExpression Visit(Expression expression)
                => (SqlExpression)sqlTranslatingExpressionVisitor.Visit(expression);
        }
    }
}