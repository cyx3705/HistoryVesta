#ifndef COFFEE_COLD_BREW_BOARD_PINS_H
#define COFFEE_COLD_BREW_BOARD_PINS_H

/*
 * Coffee cold brew machine board pin definitions.
 * Source: schematic image dated 2026-07-13.
 * Target board layout: ESP32-S3-DevKitC-1, 44-pin.
 *
 * Initialize relay outputs to CB_RELAY_SAFE_LEVEL before enabling motors.
 */

#include <stdint.h>

/* Motor channel 1. */
#define CB_PIN_MOTOR1_PWM       1
#define CB_PIN_RELAY_MOTOR1    15

/* Motor channel 2. */
#define CB_PIN_MOTOR2_PWM       2
#define CB_PIN_RELAY_MOTOR2    16

/* Human input and sensing. */
#define CB_PIN_MODE_SWITCH      4
#define CB_PIN_BATTERY_ADC      5

/* Optional on-board addressable RGB LED; verify the assembled board. */
#define CB_PIN_STATUS_RGB      38

/* NPN relay driver logic inferred from the current schematic. */
#define CB_RELAY_ACTIVE_LEVEL   1
#define CB_RELAY_SAFE_LEVEL     0

/* P1 selects GPIO4 between GND and 3V3. */
#define CB_MODE_LOW_LEVEL       0
#define CB_MODE_HIGH_LEVEL      1

/* Battery/VCC divider: R3=100k high side, R4=47k low side. */
#define CB_BATTERY_R_HIGH_OHM  100000.0f
#define CB_BATTERY_R_LOW_OHM    47000.0f
#define CB_BATTERY_DIVIDER_GAIN \
    ((CB_BATTERY_R_HIGH_OHM + CB_BATTERY_R_LOW_OHM) / CB_BATTERY_R_LOW_OHM)

static inline float cb_battery_voltage_from_adc_mv(uint32_t adc_mv)
{
    return ((float)adc_mv / 1000.0f) * CB_BATTERY_DIVIDER_GAIN;
}

#if defined(__cplusplus)
static_assert(CB_PIN_MOTOR1_PWM != CB_PIN_MOTOR2_PWM, "PWM pins must be unique");
static_assert(CB_PIN_RELAY_MOTOR1 != CB_PIN_RELAY_MOTOR2,
              "Relay pins must be unique");
static_assert(CB_PIN_BATTERY_ADC != CB_PIN_MODE_SWITCH,
              "Input pins must be unique");
#endif

#endif /* COFFEE_COLD_BREW_BOARD_PINS_H */
