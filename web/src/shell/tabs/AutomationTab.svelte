<script lang="ts">
  // The global switch and pause on top, then the per-account settings (shared defaults or one account's overrides).
  import Automation from '../../lib/sections/Automation.svelte'
  import Honor from '../../lib/sections/Honor.svelte'
  import ScopeBar from '../../lib/sections/ScopeBar.svelte'
  import Screens from '../../lib/sections/Screens.svelte'
  import AutomationCard from '../parts/AutomationCard.svelte'
</script>

<div class="tab rise">
  <h1 class="display">Automation</h1>
  <AutomationCard />
  <ScopeBar />
  <Automation />
  <Honor />
  <Screens />
</div>

<style>
  .tab { display: flex; flex-direction: column; gap: 12px; }
  h1 { font-size: 24px; padding-top: 4px; }
</style>
