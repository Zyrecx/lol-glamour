import { mount } from 'svelte'
import './app.css'
import Login from './Login.svelte'

export default mount(Login, { target: document.getElementById('app')! })
