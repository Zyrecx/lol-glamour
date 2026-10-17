<script lang="ts">
  // What Glamour does for you: one fixed column per group, all on one screen (no tabs). Narrow windows stack them.
  import Automation from '../../lib/sections/Automation.svelte'
  import Honor from '../../lib/sections/Honor.svelte'
  import ScopeBar from '../../lib/sections/ScopeBar.svelte'
  import Screens from '../../lib/sections/Screens.svelte'
  import AutomationCard from '../../shell/parts/AutomationCard.svelte'
</script>

<header class="page-head">
  <span class="eyebrow">While you play</span>
  <h1 class="display">Automation</h1>
</header>

<div class="top">
  <AutomationCard />
  <ScopeBar />
</div>

<div class="groups">
  <section class="group" aria-labelledby="g-champs">
    <h2 id="g-champs" class="eyebrow">Champion select</h2>
    <Automation part="champs" />
  </section>
  <section class="group" aria-labelledby="g-match">
    <h2 id="g-match" class="eyebrow">Match and after</h2>
    <Automation part="accept" />
    <Honor />
    <Screens />
    <Automation part="testing" />
  </section>
</div>

<style>
  .top { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 14px; align-items: start; }
  .groups { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 14px; align-items: start; }
  .group { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
  .group h2 { margin: 4px 2px 0; }
</style>
