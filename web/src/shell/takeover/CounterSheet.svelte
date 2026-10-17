<script lang="ts">
  // Counter picks on the phone (SPEC #3): a tap on a pick hovers it and closes the sheet; Lock in stays in the bar. Same view as the side panel (`counterFeed`).
  import type { CounterPickView } from '../../lib/counterPick'
  import CounterList from '../parts/CounterList.svelte'
  import Sheet from '../parts/Sheet.svelte'

  let { view, setForced, usable, onpick, onclose }: {
    view: CounterPickView; setForced: (id: number | null) => void; usable: (id: number) => boolean; onpick: (id: number) => void; onclose: () => void
  } = $props()
</script>

<Sheet label="Counter picks" {onclose} tall>
  <CounterList {view} {setForced} {usable} onpick={(id) => { onpick(id); onclose() }} />
</Sheet>
