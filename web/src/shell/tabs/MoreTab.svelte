<script lang="ts">
  // Profile extras (icon, tokens, rank and status), alerts and devices for the phone. Pairing, the password and the network are set up in the PC window (the host refuses them from a phone).
  import ClientRepair from '../../lib/sections/ClientRepair.svelte'
  import DeviceList from '../../lib/sections/DeviceList.svelte'
  import Notify from '../../lib/sections/Notify.svelte'
  import Presence from '../../lib/sections/Presence.svelte'
  import ProfileIcon from '../../lib/sections/ProfileIcon.svelte'
  import Tokens from '../../lib/sections/Tokens.svelte'
  import Toggle from '../../lib/components/Toggle.svelte'
  import { motion } from '../motion.svelte'
</script>

<div class="tab rise">
  <h1 class="display">More</h1>
  <section class="panel">
    <Toggle label="Reduce motion" hint="Stops animations and vibration." checked={motion.reduced} onchange={(v) => motion.set(v)} />
  </section>
  <ProfileIcon />
  <Tokens />
  <Presence />
  <Notify />
  <DeviceList />
  <ClientRepair />
</div>

<style>
  .tab { display: flex; flex-direction: column; gap: 12px; }
  h1 { font-size: 24px; padding-top: 4px; }
</style>
