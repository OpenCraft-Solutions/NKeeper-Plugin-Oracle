import type { ComponentType } from 'react'
import type { SessionViewProps } from '@nkeeper/sdk'

/** What the database tab is told about an engine. */
export interface DatabaseEngineInfo {
  product: string
  databaseLabel: string | null
  placeholder?: string
  blockingPermission?: string
  quote?: (node: { schema?: string | null; name?: string | null; label: string }) => string
}

/** What db-core's frontend shares (docs/architecture.md §7.32): its database tab. */
export interface DbCore {
  DatabaseView: ComponentType<SessionViewProps & { engine: DatabaseEngineInfo }>
}

let shared: DbCore | null = null

/** Keeps db-core's shared tab, from the plugin's activate. */
export function keepDbCore(dbCore: DbCore): void {
  shared = dbCore
}

export function dbCore(): DbCore | null {
  return shared
}
