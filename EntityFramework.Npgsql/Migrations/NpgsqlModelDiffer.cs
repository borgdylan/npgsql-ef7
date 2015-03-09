﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using EntityFramework.Npgsql.Extensions;
using JetBrains.Annotations;
using Microsoft.Data.Entity;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Relational.Metadata;
using Microsoft.Data.Entity.Relational.Migrations.Infrastructure;
using Microsoft.Data.Entity.Relational.Migrations.Operations;
using Microsoft.Data.Entity.Utilities;

namespace EntityFramework.Npgsql.Migrations
{
    public class NpgsqlModelDiffer : ModelDiffer
    {
        public NpgsqlModelDiffer([NotNull] NpgsqlTypeMapper typeMapper)
            : base(typeMapper)
        {
        }

        #region IModel

        private static readonly LazyRef<Sequence> _defaultSequence =
            new LazyRef<Sequence>(() => new Sequence(Sequence.DefaultName));

        protected override IEnumerable<MigrationOperation> Diff([CanBeNull] IModel source, [CanBeNull] IModel target)
        {
            var operations = base.Diff(source, target);

            // TODO: Remove when the default sequence is added to the model (See #1568)
            var sourceUsesDefaultSequence = DefaultSequenceUsed(source);
            var targetUsesDefaultSequence = DefaultSequenceUsed(target);
            if (sourceUsesDefaultSequence == false && targetUsesDefaultSequence)
            {
                operations = operations.Concat(Add(_defaultSequence.Value));
            }
            else if (sourceUsesDefaultSequence && targetUsesDefaultSequence == false)
            {
                operations = operations.Concat(Remove(_defaultSequence.Value));
            }

            return operations;
        }

        private bool DefaultSequenceUsed(IModel model) =>
            model != null
            && model.Npgsql().DefaultSequenceName == null
            && (model.Npgsql().ValueGenerationStrategy == NpgsqlValueGenerationStrategy.Sequence
                || model.EntityTypes.SelectMany(t => t.Properties).Any(
                    p => p.Npgsql().ValueGenerationStrategy == NpgsqlValueGenerationStrategy.Sequence
                         && p.Npgsql().SequenceName == null));

        #endregion

        #region IProperty

        protected override IEnumerable<MigrationOperation> Diff(IProperty source, IProperty target)
        {
            var operations = base.Diff(source, target).ToList();

            var sourceValueGenerationStrategy = GetValueGenerationStrategy(source);
            var targetValueGenerationStrategy = GetValueGenerationStrategy(target);

            var alterColumnOperation = operations.OfType<AlterColumnOperation>().SingleOrDefault();
            if (alterColumnOperation == null
                && (source.Npgsql().ComputedExpression != target.Npgsql().ComputedExpression
                    || sourceValueGenerationStrategy != targetValueGenerationStrategy))
            {
                alterColumnOperation = new AlterColumnOperation(
                    source.EntityType.Relational().Table,
                    source.EntityType.Relational().Schema,
                    new ColumnModel(
                        source.Relational().Column,
                        TypeMapper.GetTypeMapping(target).StoreTypeName,
                        target.IsNullable,
                        target.Relational().DefaultValue,
                        target.Relational().DefaultExpression));
                operations.Add(alterColumnOperation);
            }

            if (alterColumnOperation != null)
            {
                if (targetValueGenerationStrategy == NpgsqlValueGenerationStrategy.Identity)
                {
                    alterColumnOperation.Column[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.ValueGeneration] =
                        targetValueGenerationStrategy.ToString();
                }

                if (target.Npgsql().ComputedExpression != null)
                {
                    alterColumnOperation.Column[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.ColumnComputedExpression] =
                        target.Npgsql().ComputedExpression;
                }
            }

            return operations;
        }

        protected override IEnumerable<MigrationOperation> Add(IProperty target)
        {
            var operation = base.Add(target).Cast<AddColumnOperation>().Single();

            var targetValueGenerationStrategy = GetValueGenerationStrategy(target);

            if (targetValueGenerationStrategy == NpgsqlValueGenerationStrategy.Identity)
            {
                operation.Column[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.ValueGeneration] =
                    targetValueGenerationStrategy.ToString();
            }

            if (target.Npgsql().ComputedExpression != null)
            {
                operation.Column[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.ColumnComputedExpression] =
                    target.Npgsql().ComputedExpression;
            }

            yield return operation;
        }

        // TODO: Move to metadata API?
        // See Issue #1271: Principal keys need to generate values on add, but the database should only have one Identity column.
        private NpgsqlValueGenerationStrategy? GetValueGenerationStrategy(IProperty property) =>
            property.Npgsql().ValueGenerationStrategy
            ?? property.EntityType.Model.Npgsql().ValueGenerationStrategy
            ?? (property.GenerateValueOnAdd && property.PropertyType.IsInteger() && property.IsPrimaryKey()
                ? NpgsqlValueGenerationStrategy.Identity
                : default(NpgsqlValueGenerationStrategy?));

        #endregion

        #region IKey

        protected override IEnumerable<MigrationOperation> Diff(IKey source, IKey target)
        {
            var operations = base.Diff(source, target).ToList();

            var addOperation = operations.SingleOrDefault(o => o is AddPrimaryKeyOperation || o is AddUniqueConstraintOperation);
            if (addOperation == null)
            {
                if (source.Npgsql().IsClustered != target.Npgsql().IsClustered)
                {
                    if (source != null
                        && !operations.Any(o => o is DropPrimaryKeyOperation || o is DropUniqueConstraintOperation))
                    {
                        operations.AddRange(Remove(source));
                    }

                    operations.AddRange(Add(target));
                }
            }
            else if (target.Npgsql().IsClustered != null)
            {
                addOperation[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.Clustered] =
                    target.Npgsql().IsClustered.ToString();
            }

            return operations;
        }

        protected override IEnumerable<MigrationOperation> Add(IKey target)
        {
            var operation = base.Add(target)
                .Single(o => o is AddPrimaryKeyOperation || o is AddUniqueConstraintOperation);

            if (target.Npgsql().IsClustered != null)
            {
                operation[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.Clustered] =
                    target.Npgsql().IsClustered.ToString();
            }

            yield return operation;
        }

        #endregion

        #region IIndex

        protected override IEnumerable<MigrationOperation> Diff(IIndex source, IIndex target)
        {
            var operations = base.Diff(source, target).ToList();

            var createIndexOperation = operations.OfType<CreateIndexOperation>().SingleOrDefault();
            if (createIndexOperation == null
                && source.Npgsql().IsClustered != target.Npgsql().IsClustered)
            {
                operations.AddRange(Remove(source));

                createIndexOperation = new CreateIndexOperation(
                    target.Relational().Name,
                    target.EntityType.Relational().Table,
                    target.EntityType.Relational().Schema,
                    target.Properties.Select(p => p.Relational().Column).ToArray(),
                    target.IsUnique);
                operations.Add(createIndexOperation);
            }

            if (createIndexOperation != null
                && target.Npgsql().IsClustered != null)
            {
                createIndexOperation[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.Clustered] =
                    target.Npgsql().IsClustered.ToString();
            }

            return operations;
        }

        protected override IEnumerable<MigrationOperation> Add(IIndex target)
        {
            var operation = base.Add(target).Cast<CreateIndexOperation>().Single();

            if (target.Npgsql().IsClustered != null)
            {
                operation[NpgsqlAnnotationNames.Prefix + NpgsqlAnnotationNames.Clustered] =
                    target.Npgsql().IsClustered.ToString();
            }

            return base.Add(target);
        }

        #endregion
    }
}
