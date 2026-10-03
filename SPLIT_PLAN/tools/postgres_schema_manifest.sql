-- Run only against a disposable database initialized by Full's migrations.
-- Excludes FluentMigrator's history and Cleipnir's independently managed runtime.
SELECT jsonb_pretty(jsonb_build_object(
 'postgresMigrationVersion', (SELECT max("Version") FROM public."VersionInfo"),
 'enums', (SELECT jsonb_object_agg(name, labels ORDER BY name) FROM (
   SELECT n.nspname || '.' || t.typname AS name, jsonb_agg(e.enumlabel ORDER BY e.enumsortorder) AS labels
   FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace JOIN pg_enum e ON e.enumtypid=t.oid
   WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') GROUP BY n.nspname,t.typname) enums),
 'tables', (SELECT jsonb_agg(jsonb_build_object(
   'schema', n.nspname, 'name', c.relname,
   'columns', (SELECT jsonb_agg(jsonb_build_object(
      'name', a.attname, 'type', format_type(a.atttypid,a.atttypmod),
      'nullable', NOT a.attnotnull, 'default', pg_get_expr(d.adbin,d.adrelid)) ORDER BY a.attnum)
      FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
      WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped),
   'constraints', (SELECT coalesce(jsonb_agg(jsonb_build_object(
      'name', k.conname, 'kind', k.contype, 'definition', pg_get_constraintdef(k.oid),
      'columns', (SELECT jsonb_agg(a.attname ORDER BY pos) FROM unnest(k.conkey) WITH ORDINALITY x(attnum,pos) JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=x.attnum),
      'targetSchema', tn.nspname, 'targetTable', tc.relname,
      'targetColumns', (SELECT jsonb_agg(a.attname ORDER BY pos) FROM unnest(k.confkey) WITH ORDINALITY x(attnum,pos) JOIN pg_attribute a ON a.attrelid=k.confrelid AND a.attnum=x.attnum),
      'onDelete', k.confdeltype, 'onUpdate', k.confupdtype) ORDER BY k.conname), '[]'::jsonb)
      FROM pg_constraint k LEFT JOIN pg_class tc ON tc.oid=k.confrelid LEFT JOIN pg_namespace tn ON tn.oid=tc.relnamespace WHERE k.conrelid=c.oid AND k.contype <> 'n'),
   'indexes', (SELECT coalesce(jsonb_agg(jsonb_build_object(
      'name', ic.relname, 'unique', i.indisunique, 'primary', i.indisprimary,
      'definition', pg_get_indexdef(i.indexrelid), 'predicate', pg_get_expr(i.indpred,i.indrelid),
      'columns', (SELECT jsonb_agg(pg_get_indexdef(i.indexrelid,pos,true) ORDER BY pos) FROM generate_series(1,i.indnkeyatts) pos)) ORDER BY ic.relname), '[]'::jsonb)
      FROM pg_index i JOIN pg_class ic ON ic.oid=i.indexrelid WHERE i.indrelid=c.oid)
   ) ORDER BY n.nspname,c.relname)
   FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE c.relkind='r' AND n.nspname NOT IN ('pg_catalog','information_schema','cleipnir','public'))
));
