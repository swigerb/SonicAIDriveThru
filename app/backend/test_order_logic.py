import asyncio
from order_state import order_state_singleton

async def test_combo_logic():
    # 1. Start a new session
    session_id = order_state_singleton.create_session()
    print(f"--- Starting Test for Session: {session_id} ---")

    # 2. Add two Burgers (Combos)
    # Scenario: Customer says "Give me two Number 1 combos"
    order_state_singleton.handle_order_update(
        session_id, "add", "Sonic Cheeseburger Combo", "Medium", 2, 8.99
    )
    
    # 3. Check requirements
    status = order_state_singleton.get_combo_requirements(session_id)
    print(f"After 2 Burgers: {status['prompt_hint']}")
    # EXPECTED: "Ask the guest for a side (fries or tots), and a drink or slush..."

    # 4. Add only ONE side
    # Scenario: Customer says "I'll take Tots with that"
    order_state_singleton.handle_order_update(
        session_id, "add", "Tots", "Medium", 1, 2.49
    )
    
    status = order_state_singleton.get_combo_requirements(session_id)
    print(f"After 1 Tot: {status['prompt_hint']}")
    # EXPECTED: Still shows missing items because we have 2 combos but only 1 side.

    # 5. Get the Grouped Readback
    readback = order_state_singleton.get_grouped_order_for_readback(session_id)
    print(f"Readback: {readback}")

if __name__ == "__main__":
    asyncio.run(test_combo_logic())
