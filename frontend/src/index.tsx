import { definePlugin } from '@nkeeper/sdk'
import { keepDbCore, type DbCore } from './dbCore'
import { OracleView } from './OracleView'

/**
 * The oracle plugin's frontend (docs/architecture.md §7.34): it draws its session type with db-core's database tab,
 * which db-core shares with the engines that depend on it (SDK 1.8).
 */
const plugin = definePlugin({
  id: 'oracle',
  activate(context) {
    const shared = context.use('db-core')
    if (typeof shared.DatabaseView !== 'function' && typeof shared.DatabaseView !== 'object') {
      throw new Error('db-core shares no database tab: it is older than this plugin needs.')
    }
    keepDbCore(shared as unknown as DbCore)
  },
  sessionViews: { oracle: OracleView },
})

export default plugin
