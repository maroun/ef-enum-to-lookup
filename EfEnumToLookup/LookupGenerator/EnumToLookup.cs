﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("EFTests")]

namespace EfEnumToLookup.LookupGenerator
{
    public class EnumToLookup : IEnumToLookup
    {
        public EnumToLookup()
        {
            NameFieldLength = 255; // default
            TableNamePrefix = "Enum_";
        }

        /// <summary>
        /// The size of the Name field that will be added to the generated lookup tables.
        /// Adjust to suit your data if required, defaults to 255.
        /// </summary>
        public int NameFieldLength { get; set; }

        /// <summary>
        /// Prefix to add to all the generated tables to separate help group them together
        /// and make them stand out as different from other tables.
        /// Defaults to "Enum_" set to null or "" to not have any prefix.
        /// </summary>
        public string TableNamePrefix { get; set; }

        public void Apply(DbContext context)
        {
            // recurese through dbsets and references finding anything that uses an enum
            var refs = FindReferences(context.GetType());
            // for the list of enums generate tables
            var enums = refs.Select(r => r.EnumType).Distinct().ToList();
            CreateTables(enums, (sql) => context.Database.ExecuteSqlCommand(sql));
            // t-sql merge values into table
            PopulateLookups(enums, (sql) => context.Database.ExecuteSqlCommand(sql));
            // add fks from all referencing tables
        }

        private void PopulateLookups(IEnumerable<Type> enums, Action<string> runSql)
        {
            foreach (var lookup in enums)
            {
                PopulateLookup(lookup, runSql);
            }
        }

        private void PopulateLookup(Type lookup, Action<string> runSql)
        {
            if (!lookup.IsEnum)
            {
                throw new ArgumentException("Lookup type must be an enum", "lookup");
            }

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("CREATE TABLE #lookups (Id int, Name nvarchar({0}));", NameFieldLength));
            foreach (var value in Enum.GetValues(lookup))
            {
                var id = (int)value;
                var name = value.ToString();
                sb.AppendLine(string.Format("INSERT INTO #lookups (Id, Name) VALUES ({0}, '{1}');", id, name));
            }

            sb.AppendLine(string.Format(@"
MERGE INTO [{0}] dst
	USING #lookups src ON src.Id = dst.Id
	WHEN MATCHED AND src.Name <> dst.Name THEN
		UPDATE SET Name = src.Name
	WHEN NOT MATCHED THEN
		INSERT (Id, Name)
		VALUES (src.Id, src.Name)
	WHEN NOT MATCHED BY SOURCE THEN
		DELETE
;"
                , TableName(lookup.Name)));

            sb.AppendLine("DROP TABLE #lookups;");
            runSql(sb.ToString());
        }

        private void CreateTables(IEnumerable<Type> enums, Action<string> runSql)
        {
            foreach (var lookup in enums)
            {
                runSql(string.Format(
                    @"CREATE TABLE [{0}] (Id int, Name nvarchar({1}));",
                    TableName(lookup.Name), NameFieldLength));
            }
        }

        private string TableName(string enumName)
        {
            return string.Format("{0}{1}", TableNamePrefix, enumName);
        }

        internal IList<EnumReference> FindReferences(Type contextType)
        {
            var dbSets = FindDbSets(contextType);
            var enumReferences = new List<EnumReference>();
            foreach (var dbSet in dbSets)
            {
                var dbSetType = DbSetType(dbSet);
                var enumProperties = FindEnums(dbSetType);
                enumReferences.AddRange(enumProperties
                    .Select(enumProp => new EnumReference
                        {
                            // todo: apply fluent / attribute name changes
                            ReferencingTable = dbSet.Name,
                            ReferencingField = enumProp.Name,
                            EnumType = UnwrapIfNullable(enumProp.PropertyType),
                        }
                    ));
            }
            return enumReferences;
        }

        private static Type UnwrapIfNullable(Type type)
        {
            if (!type.IsGenericType)
            {
                return type;
            }
            if (type.GetGenericTypeDefinition() != typeof(Nullable<>))
            {
                throw new NotSupportedException(string.Format("Unexpected generic enum type in model: {0}, expected non-generic or nullable.", type));
            }
            return type.GenericTypeArguments.First();
        }

        /// <summary>
        /// Unwraps the type inside a DbSet&lt;&gt;
        /// </summary>
        private static Type DbSetType(PropertyInfo dbSet)
        {
            return dbSet.PropertyType.GenericTypeArguments.First();
        }

        internal IList<PropertyInfo> FindDbSets(Type contextType)
        {
            return contextType.GetProperties()
                .Where(p => p.PropertyType.IsGenericType
                    && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .ToList();
        }

        public IList<PropertyInfo> FindEnums(Type type)
        {
            return type.GetProperties()
                .Where(p => p.PropertyType.IsEnum
                    || (p.PropertyType.IsGenericType && p.PropertyType.GenericTypeArguments.First().IsEnum))
                .ToList();
        }
    }
}
