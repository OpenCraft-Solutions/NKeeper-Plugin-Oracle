import type { SessionViewProps } from '@nkeeper/sdk'
import { dbCore, type DatabaseEngineInfo } from './dbCore'

/** "SCHEMA"."NAME", with " doubled, as Oracle quotes names. */
function quote(node: { schema?: string | null; name?: string | null; label: string }): string {
  const one = (name: string) => `"${name.replace(/"/g, '""')}"`
  return node.schema && node.name ? `${one(node.schema)}.${one(node.name)}` : one(node.name ?? node.label)
}

const ORACLE: DatabaseEngineInfo = {
  product: 'Oracle',
  databaseLabel: 'Schema',
  placeholder: 'SELECT * FROM user_tables;',
  blockingPermission: 'SELECT_CATALOG_ROLE',
  quote,
}

/** The Oracle tab: db-core's database tab, told it's Oracle. */
export function OracleView(props: SessionViewProps) {
  const shared = dbCore()
  return shared ? <shared.DatabaseView {...props} engine={ORACLE} /> : null
}
